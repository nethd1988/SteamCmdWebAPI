using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Timers;
using System.Text;

namespace SteamCmdWebAPI.Services
{
    public class SteamCmdService
    {
        private readonly ILogger<SteamCmdService> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly ProfileService _profileService;
        private readonly SettingsService _settingsService;
        private readonly EncryptionService _encryptionService;
        private readonly LogFileReader _logFileReader;

        private const int MaxLogEntries = 5000;

        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private readonly object _processLock = new object();

        private readonly System.Timers.Timer _scheduleTimer;
        private volatile bool _isRunningAllProfiles = false;
        private int _currentProfileIndex = 0;
        private volatile bool _cancelAutoRun = false;

        private DateTime _lastAutoRunTime = DateTime.MinValue;

        private readonly List<LogEntry> _logs = new List<LogEntry>(MaxLogEntries);

        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string ProfileName { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }

            public LogEntry(DateTime timestamp, string profileName, string status, string message)
            {
                Timestamp = timestamp;
                ProfileName = profileName;
                Status = status;
                Message = message;
            }
        }

        public SteamCmdService(
            ILogger<SteamCmdService> logger,
            IHubContext<LogHub> hubContext,
            ProfileService profileService,
            SettingsService settingsService,
            EncryptionService encryptionService,
            LogFileReader logFileReader)
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _settingsService = settingsService;
            _encryptionService = encryptionService;
            _logFileReader = logFileReader;

            _scheduleTimer = new System.Timers.Timer(60000);
            _scheduleTimer.Elapsed += async (s, e) => await CheckScheduleAsync();
            _scheduleTimer.AutoReset = true;
            _scheduleTimer.Start();
            _logger.LogInformation("Schedule timer started.");
        }

        private void AddLog(LogEntry entry)
        {
            lock (_logs)
            {
                _logs.Add(entry);
                if (_logs.Count > MaxLogEntries)
                {
                    _logs.RemoveRange(0, _logs.Count - MaxLogEntries);
                }
            }
        }

        private async Task CheckScheduleAsync()
        {
            try
            {
                var settings = await _settingsService.LoadSettingsAsync();
                if (!settings.AutoRunEnabled || _isRunningAllProfiles)
                {
                    return;
                }

                var now = DateTime.Now;
                TimeSpan timeSinceLastRun = now - _lastAutoRunTime;
                int intervalHours = settings.AutoRunIntervalHours;

                if (_lastAutoRunTime == DateTime.MinValue || timeSinceLastRun.TotalHours >= intervalHours)
                {
                    _logger.LogInformation("Đang chạy tất cả profile theo khoảng thời gian {0} giờ", intervalHours);
                    await RunAllProfilesAsync();
                    _lastAutoRunTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra lịch hẹn");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi kiểm tra lịch hẹn: {ex.Message}");
            }
        }

        private async Task SafeSendLogAsync(string profileName, string status, string message)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveLog", message);
                AddLog(new LogEntry(DateTime.Now, profileName, status, message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi log: {Message}", ex.Message);
            }
        }

        #region Process Management

        public async Task<bool> StartProfileAsync(int profileId)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogError("Không tìm thấy profile ID {ProfileId}", profileId);
                    await SafeSendLogAsync($"Profile {profileId}", "Error", $"Không tìm thấy profile với ID {profileId}");
                    return false;
                }

                await SafeSendLogAsync(profile.Name, "Info", $"Đang chuẩn bị chạy {profile.Name}...");

                KillAllSteamCmdProcessesImmediate();
                await Task.Delay(1000);

                await RemoveSteamAppsSymLink();
                await Task.Delay(1000);

                try
                {
                    profile.Status = "Running";
                    profile.StartTime = DateTime.Now;
                    profile.Pid = 0;
                    await _profileService.UpdateProfile(profile);

                    await SafeSendLogAsync(profile.Name, "Info", $"Đang khởi động {profile.Name}...");

                    bool steamCmdInstalled = await IsSteamCmdInstalled();
                    if (!steamCmdInstalled)
                    {
                        await SafeSendLogAsync(profile.Name, "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                        await InstallSteamCmd();

                        steamCmdInstalled = await IsSteamCmdInstalled();
                        if (!steamCmdInstalled)
                        {
                            string errorMsg = "Không thể cài đặt hoặc tìm thấy SteamCMD";
                            _logger.LogError(errorMsg);
                            await SafeSendLogAsync(profile.Name, "Error", errorMsg);

                            profile.Status = "Stopped";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                            return false;
                        }
                    }

                    if (!string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        if (!Directory.Exists(profile.InstallDirectory))
                        {
                            try
                            {
                                Directory.CreateDirectory(profile.InstallDirectory);
                                _logger.LogInformation("Đã tạo thư mục cài đặt: {Directory}", profile.InstallDirectory);
                                await SafeSendLogAsync(profile.Name, "Info", $"Đã tạo thư mục cài đặt: {profile.InstallDirectory}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Không thể tạo thư mục cài đặt: {Directory}", profile.InstallDirectory);
                                throw new Exception($"Không thể tạo thư mục cài đặt {profile.InstallDirectory}: {ex.Message}");
                            }
                        }

                        try
                        {
                            string testFilePath = Path.Combine(profile.InstallDirectory, "writetest.tmp");
                            File.WriteAllText(testFilePath, "test");
                            File.Delete(testFilePath);
                        }
                        catch (Exception ex)
                        {
                            //_logger.LogError(ex, "Không có quyền ghi vào thư mục cài đặt: {Directory}", profile.InstallDirectory);
                            //await SafeSendLogAsync(profile.Name, "Error", $"Không có quyền ghi vào thư mục cài đặt: {ex.Message}");

                            profile.Status = "Stopped";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                            return false;
                        }
                    }

                    bool success = await RunSteamCmdAndWaitAsync(GetSteamCmdPath(), profile, profileId);

                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0;
                    profile.LastRun = DateTime.UtcNow;
                    await _profileService.UpdateProfile(profile);

                    if (success)
                    {
                        await SafeSendLogAsync(profile.Name, "Success", $"Đã chạy {profile.Name} thành công");
                    }
                    else
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"Chạy {profile.Name} không thành công");
                    }

                    return success;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy profile {ProfileId}: {Message}", profileId, ex.Message);

                try
                {
                    var profile = await _profileService.GetProfileById(profileId);
                    if (profile != null)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy {profile.Name}: {ex.Message}");
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        profile.Pid = 0;
                        await _profileService.UpdateProfile(profile);
                    }
                }
                catch { }

                await RemoveSteamAppsSymLink();
                KillAllSteamCmdProcessesImmediate();
                _steamCmdProcesses.TryRemove(profileId, out _);

                return false;
            }
        }

        private void KillAllSteamCmdProcessesImmediate()
        {
            try
            {
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    var profileId = kvp.Key;
                    var process = kvp.Value;
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            _logger.LogInformation("Đang kill ngay lập tức tiến trình SteamCMD với PID {Pid} cho profile {ProfileId}",
                                process.Id, profileId);

                            process.Kill(true);
                            process.WaitForExit(2000);

                            if (!process.HasExited)
                            {
                                _logger.LogWarning("Tiến trình không kết thúc sau 2s, tiếp tục chờ tiến trình thoát");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi khi kill tiến trình SteamCMD cho profile {ProfileId}", profileId);
                    }
                    finally
                    {
                        process?.Dispose();
                        _steamCmdProcesses.TryRemove(profileId, out _);
                    }
                }

                try
                {
                    foreach (var process in Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")))
                    {
                        try
                        {
                            _logger.LogWarning("Phát hiện tiến trình SteamCMD thất lạc với PID {Pid}, đang kill", process.Id);
                            process.Kill(true);
                            process.WaitForExit(2000);
                            process.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi kill tiến trình SteamCMD thất lạc với PID {Pid}", process.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tìm và kill các tiến trình SteamCMD thất lạc");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kill tất cả tiến trình SteamCMD ngay lập tức");
            }
        }

        public async Task StopAllProfilesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các profiles...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profiles...");

            _cancelAutoRun = true;

            var profileIds = _steamCmdProcesses.Keys.ToList();

            foreach (var profileId in profileIds)
            {
                await StopAndCleanupProfileAsync(profileId);
            }

            await KillOrphanedSteamCmdProcesses();

            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.Status == "Running"))
            {
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);
            }

            await RemoveSteamAppsSymLink();

            await SafeSendLogAsync("System", "Success", "Đã dừng tất cả các profiles");
        }

        private async Task StopAndCleanupProfileAsync(int profileId)
        {
            if (_steamCmdProcesses.TryRemove(profileId, out var existingProcess))
            {
                try
                {
                    if (existingProcess != null && !existingProcess.HasExited)
                    {
                        _logger.LogInformation("Đang dừng tiến trình đang chạy cho profile {ProfileId}, PID: {Pid}",
                            profileId, existingProcess.Id);
                        await SafeSendLogAsync($"Profile {profileId}", "Info", $"Đang dừng tiến trình đang chạy (PID: {existingProcess.Id})");

                        existingProcess.Kill(true);
                        existingProcess.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileId}", profileId);
                }
                finally
                {
                    existingProcess?.Dispose();
                }
            }

            await RemoveSteamAppsSymLink();
            await KillOrphanedSteamCmdProcesses();
        }

        private async Task KillOrphanedSteamCmdProcesses()
        {
            try
            {
                var steamCmdProcesses = Process.GetProcessesByName("steamcmd");
                if (steamCmdProcesses.Length > 0)
                {
                    _logger.LogWarning("Phát hiện {Count} tiến trình SteamCMD bị thất lạc, đang dọn dẹp", steamCmdProcesses.Length);
                    await SafeSendLogAsync("System", "Warning", $"Phát hiện {steamCmdProcesses.Length} tiến trình SteamCMD bị thất lạc, đang dọn dẹp");

                    foreach (var process in steamCmdProcesses)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                _logger.LogInformation("Đang kill tiến trình SteamCMD thất lạc, PID: {Pid}", process.Id);
                                process.Kill(true);
                                process.WaitForExit(2000);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi kill tiến trình SteamCMD thất lạc, PID: {Pid}", process.Id);
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tìm và kill các tiến trình SteamCMD thất lạc");
            }
        }

        #endregion

        #region SteamCMD Installation and Management

        public async Task<bool> RunProfileAsync(int id)
        {
            try
            {
                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    // Giảm thiểu log
                    return false;
                }

                return await StartProfileAsync(id);
            }
            catch (Exception ex)
            {
                // Ghi log ở mức Debug
                _logger.LogDebug(ex, "Lỗi khi chạy SteamCMD cho profile ID {Id}", id);
                throw;
            }
        }

        public async Task RunAllProfilesAsync()
        {
            if (_isRunningAllProfiles)
            {
                await SafeSendLogAsync("System", "Warning", "Đang có quá trình chạy tất cả profile, không thể thực hiện yêu cầu này");
                return;
            }

            try
            {
                var profiles = await _profileService.GetAllProfiles();
                if (!profiles.Any())
                {
                    _logger.LogWarning("Không có cấu hình nào để chạy");
                    await SafeSendLogAsync("System", "Error", "Không có cấu hình nào để chạy");
                    return;
                }

                _isRunningAllProfiles = true;
                _cancelAutoRun = false;
                _currentProfileIndex = 0;

                await SafeSendLogAsync("System", "Info", "Bắt đầu chạy tất cả các profile...");

                await StopAllProfilesAsync();
                await Task.Delay(3000);

                KillAllSteamCmdProcessesImmediate();
                await RemoveSteamAppsSymLink();

                foreach (var profile in profiles)
                {
                    if (_cancelAutoRun) break;

                    _currentProfileIndex++;
                    await SafeSendLogAsync(profile.Name, "Info",
                        $"Đang chạy profile ({_currentProfileIndex}/{profiles.Count}): {profile.Name}");

                    try
                    {
                        bool success = await StartProfileAsync(profile.Id);
                        if (!success)
                        {
                            await SafeSendLogAsync(profile.Name, "Warning",
                                $"Chạy profile {profile.Name} không thành công");
                        }

                        if (_currentProfileIndex < profiles.Count)
                        {
                            await Task.Delay(5000);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi chạy profile {ProfileName}", profile.Name);
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi: {ex.Message}");
                    }
                }

                await SafeSendLogAsync("System", "Success", "Đã hoàn thành chạy tất cả các profile");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tất cả các profile");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chạy tất cả các profile: {ex.Message}");
            }
            finally
            {
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
            }
        }

        public Task<bool> IsSteamCmdInstalled()
        {
            string steamCmdPath = GetSteamCmdPath();
            bool exists = File.Exists(steamCmdPath);
            _logger.LogInformation("Kiểm tra SteamCMD tại {Path}: {Exists}", steamCmdPath, exists);

            if (exists)
            {
                _logger.LogInformation("SteamCMD đã được cài đặt tại: {Path}", steamCmdPath);
            }
            else
            {
                _logger.LogWarning("Không tìm thấy SteamCMD tại: {Path}", steamCmdPath);
            }

            return Task.FromResult(exists);
        }

        public async Task InstallSteamCmd()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            try
            {
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd: {Directory}", steamCmdDir);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamcmd: {steamCmdDir}");
                }

                string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");
                string downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

                if (!OperatingSystem.IsWindows())
                {
                    downloadUrl = OperatingSystem.IsLinux()
                        ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz"
                        : OperatingSystem.IsMacOS()
                            ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz"
                            : throw new PlatformNotSupportedException("Hệ điều hành không được hỗ trợ");
                }

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);

                    await SafeSendLogAsync("System", "Info", $"Đang tải SteamCMD từ {downloadUrl}...");
                    _logger.LogInformation("Bắt đầu tải SteamCMD từ {Url}", downloadUrl);

                    var response = await httpClient.GetAsync(downloadUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception($"Không thể tải SteamCMD. Mã trạng thái: {response.StatusCode}");
                    }

                    using (var fs = new FileStream(zipPath, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }

                    await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                    _logger.LogInformation("Đã tải xong SteamCMD vào {Path}", zipPath);
                }

                bool extractSuccess = await ExtractSteamCmdAsync(zipPath, steamCmdDir);
                if (!extractSuccess)
                {
                    throw new Exception("Không thể giải nén SteamCMD");
                }

                await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công");
                _logger.LogInformation("Đã giải nén SteamCMD vào {Directory}", steamCmdDir);

                try
                {
                    File.Delete(zipPath);
                    _logger.LogInformation("Đã xóa file zip: {Path}", zipPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể xóa file zip: {Path}", zipPath);
                }

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    await SetExecutablePermission(GetSteamCmdPath());
                }

                string steamCmdExe = GetSteamCmdPath();
                if (!File.Exists(steamCmdExe))
                {
                    throw new Exception($"Cài đặt thất bại. Không tìm thấy {steamCmdExe} sau khi cài đặt.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình cài đặt SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi cài đặt SteamCMD: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> ExtractSteamCmdAsync(string zipPath, string destinationFolder)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destinationFolder, true);
                    return true;
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    using (var process = new Process())
                    {
                        process.StartInfo = new ProcessStartInfo
                        {
                            FileName = "tar",
                            Arguments = $"-xzf \"{zipPath}\" -C \"{destinationFolder}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        process.Start();

                        var outputTask = process.StandardOutput.ReadToEndAsync();
                        var errorTask = process.StandardError.ReadToEndAsync();

                        if (!process.WaitForExit(60000))
                        {
                            process.Kill();
                            throw new TimeoutException("Quá thời gian khi giải nén SteamCMD.");
                        }

                        string error = await errorTask;
                        if (process.ExitCode != 0)
                        {
                            throw new Exception($"Lỗi khi giải nén SteamCMD: {error}");
                        }

                        return true;
                    }
                }
                throw new PlatformNotSupportedException("Hệ điều hành không được hỗ trợ");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi giải nén SteamCMD");
                throw;
            }
        }

        private async Task SetExecutablePermission(string filePath)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    process.Start();

                    if (!process.WaitForExit(10000))
                    {
                        process.Kill();
                        _logger.LogWarning("Quá thời gian khi thiết lập quyền thực thi cho SteamCMD");
                        return;
                    }

                    if (process.ExitCode != 0)
                    {
                        string error = await process.StandardError.ReadToEndAsync();
                        _logger.LogWarning("Không thể cấp quyền thực thi cho SteamCMD: {Error}", error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể cấp quyền thực thi cho SteamCMD");
            }
        }

        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string executable = OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh";
            string path = Path.Combine(steamCmdDir, executable);

            return path;
        }

        private async Task CreateSteamAppsSymLink(string gameInstallDir)
        {
            string gameSteamAppsPath = Path.Combine(gameInstallDir, "steamapps");
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            try
            {
                // Xóa thư mục steamapps tại localSteamAppsPath nếu tồn tại
                if (Directory.Exists(localSteamAppsPath))
                {
                    try
                    {
                        Directory.Delete(localSteamAppsPath, true); // Xóa mạnh mẽ, bao gồm tất cả tệp và thư mục con
                        //await SafeSendLogAsync("System", "Info", $"Đã xóa thư mục steamapps tại {localSteamAppsPath}");
                    }
                    catch (Exception ex)
                    {
                        await SafeSendLogAsync("System", "Error", $"Không thể xóa thư mục steamapps tại {localSteamAppsPath}: {ex.Message}");
                        throw; // Ném lỗi để dừng quá trình nếu không xóa được
                    }
                }

                // Tạo thư mục steamapps trong thư mục cài đặt game nếu chưa tồn tại
                if (!Directory.Exists(gameSteamAppsPath))
                {
                    Directory.CreateDirectory(gameSteamAppsPath);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamapps tại {gameSteamAppsPath}");
                }

                bool success = false;

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        if (Directory.CreateSymbolicLink(localSteamAppsPath, gameSteamAppsPath) != null)
                        {
                            success = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể sử dụng Directory.CreateSymbolicLink, sẽ thử dùng process");
                    }
                }

                if (!success)
                {
                    success = await CreateSymbolicLinkWithProcess(gameSteamAppsPath, localSteamAppsPath);

                    if (success)
                    {
                        // Đã tạo symbolic link thành công
                    }
                    else
                    {
                        throw new Exception("Không thể tạo symbolic link sau nhiều lần thử");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo symbolic link: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi tạo symbolic link: {ex.Message}");
                throw;
            }
        }

        private bool IsSymbolicLink(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        private void DeleteSymbolicLink(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.Delete(path);
            }
            else
            {
                RunProcessWithTimeout("rm", $"-f \"{path}\"", 10000);
            }
        }

        private async Task<bool> CreateSymbolicLinkWithProcess(string targetPath, string linkPath)
        {
            try
            {
                ProcessStartInfo linkInfo;
                if (OperatingSystem.IsWindows())
                {
                    linkInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C mklink /D \"{linkPath}\" \"{targetPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    linkInfo = new ProcessStartInfo
                    {
                        FileName = "ln",
                        Arguments = $"-s \"{targetPath}\" \"{linkPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }
                else
                {
                    throw new PlatformNotSupportedException("Hệ điều hành không được hỗ trợ để tạo symbolic link");
                }

                using (var process = Process.Start(linkInfo))
                {
                    if (process == null)
                    {
                        throw new Exception("Không thể khởi động tiến trình để tạo symbolic link.");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("Quá thời gian khi tạo symbolic link.");
                    }

                    string output = await outputTask;
                    string error = await errorTask;

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("Lỗi khi tạo symbolic link: {Error}", error);
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo symbolic link với process");
                return false;
            }
        }

        private void RunProcessWithTimeout(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    process.Start();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        _logger.LogWarning("Quá thời gian chạy process {FileName} {Arguments}", fileName, arguments);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy process {FileName} {Arguments}", fileName, arguments);
            }
        }

        private async Task RemoveSteamAppsSymLink()
        {
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            try
            {
                if (Directory.Exists(localSteamAppsPath))
                {
                    try
                    {
                        if (IsSymbolicLink(localSteamAppsPath))
                        {
                            _logger.LogInformation("Đang xóa symbolic link tại {Path}", localSteamAppsPath);
                            DeleteSymbolicLink(localSteamAppsPath);
                        }
                        else
                        {
                            Directory.Delete(localSteamAppsPath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể xóa symbolic link/thư mục theo cách thông thường, thử phương pháp thay thế");
                        try
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                RunProcessWithTimeout("cmd.exe", $"/C rmdir \"{localSteamAppsPath}\"", 10000);
                            }
                            else
                            {
                                RunProcessWithTimeout("rm", $"-rf \"{localSteamAppsPath}\"", 10000);
                            }
                        }
                        catch (Exception innerEx)
                        {
                            _logger.LogError(innerEx, "Không thể xóa symbolic link/thư mục bằng phương pháp thay thế tại {Path}", localSteamAppsPath);
                            await SafeSendLogAsync("System", "Error", $"Không thể xóa tại {localSteamAppsPath}: {innerEx.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa symbolic link/thư mục tại {Path}: {Message}", localSteamAppsPath, ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi xóa tại {localSteamAppsPath}: {ex.Message}");
            }
        }

        private string GetSteamCmdLogPath(int profileId)
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string logsDir = Path.Combine(steamCmdDir, "logs");

            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            return Path.Combine(logsDir, $"console_log_{profileId}.txt");
        }

        private async Task<bool> RunSteamCmdAndWaitAsync(string steamCmdPath, SteamCmdProfile profile, int profileId)
        {
            Process steamCmdProcess = null;
            bool success = false;

            try
            {
                if (string.IsNullOrEmpty(steamCmdPath) || !File.Exists(steamCmdPath))
                {
                    _logger.LogError("File SteamCMD không tồn tại tại {Path}", steamCmdPath);
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại tại {steamCmdPath}");
                    return false;
                }

                await CreateSteamAppsSymLink(profile.InstallDirectory);
                await Task.Delay(1000);

                string logFilePath = GetSteamCmdLogPath(profileId);

                string logsDir = Path.GetDirectoryName(logFilePath);
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }

                _logFileReader.StartMonitoring(logFilePath, async (content) =>
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", content);
                    }
                });

                try
                {
                    string loginCommand = "+login anonymous";

                    if (!string.IsNullOrEmpty(profile.SteamUsername) && !string.IsNullOrEmpty(profile.SteamPassword))
                    {
                        string usernameToUse = "";
                        string passwordToUse = "";

                        try
                        {
                            usernameToUse = _encryptionService.Decrypt(profile.SteamUsername);
                            passwordToUse = _encryptionService.Decrypt(profile.SteamPassword);
                            loginCommand = $"+login {usernameToUse} {passwordToUse}";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã thông tin đăng nhập cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã thông tin đăng nhập: {ex.Message}");
                            loginCommand = "+login anonymous";
                        }
                    }

                    string validateArg = profile.ValidateFiles ? "validate" : "";
                    string argumentsArg = string.IsNullOrEmpty(profile.Arguments) ? "" : profile.Arguments.Trim();
                    string arguments = $"{loginCommand} +app_update {profile.AppID} {validateArg} {argumentsArg} +quit".Trim();

                    string safeArguments = arguments.Contains("+login ") ?
                        arguments.Replace("+login ", "+login [credentials] ") :
                        arguments;

                    _logger.LogInformation("Chạy SteamCMD với tham số: {SafeArguments}", safeArguments);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy SteamCMD cho {profile.Name}...");

                    steamCmdProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = steamCmdPath,
                            Arguments = arguments,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            RedirectStandardInput = true,
                            CreateNoWindow = true,
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8
                        },
                        EnableRaisingEvents = true
                    };

                    var outputBuffer = new StringBuilder();
                    var outputTimer = new System.Timers.Timer(100);
                    outputTimer.Elapsed += async (sender, e) =>
                    {
                        string output;
                        lock (outputBuffer)
                        {
                            if (outputBuffer.Length == 0) return;
                            output = outputBuffer.ToString();
                            outputBuffer.Clear();
                        }
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", output);
                    };
                    outputTimer.Start();

                    steamCmdProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            _logger.LogDebug("SteamCMD Output: {Data}", e.Data);
                            lock (outputBuffer)
                            {
                                outputBuffer.AppendLine(e.Data);
                            }
                        }
                    };

                    steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            _logger.LogError("SteamCMD Error: {Data}", e.Data);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", e.Data);
                        }
                    };

                    _steamCmdProcesses[profileId] = steamCmdProcess;

                    steamCmdProcess.Start();

                    profile.Pid = steamCmdProcess.Id;
                    await _profileService.UpdateProfile(profile);

                    steamCmdProcess.BeginOutputReadLine();
                    steamCmdProcess.BeginErrorReadLine();

                    await Task.Run(() => steamCmdProcess.WaitForExit());

                    outputTimer.Stop();
                    string remainingOutput;
                    lock (outputBuffer)
                    {
                        remainingOutput = outputBuffer.ToString();
                        outputBuffer.Clear();
                    }
                    if (!string.IsNullOrEmpty(remainingOutput))
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", remainingOutput);
                    }

                    int exitCode = steamCmdProcess.ExitCode;
                    _logger.LogInformation("SteamCMD process đã kết thúc với exit code: {ExitCode}", exitCode);

                    string exitMessage = $"SteamCMD đã kết thúc với mã: {exitCode} ({(exitCode == 0 ? "Thành công" : "Lỗi")})";
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);

                    AddLog(new LogEntry(DateTime.Now, profile.Name,
                        exitCode == 0 ? "Success" : "Error",
                        exitMessage));

                    success = (exitCode == 0);
                }
                finally
                {
                    _logFileReader.StopMonitoring();

                    _steamCmdProcesses.TryRemove(profileId, out _);

                    await RemoveSteamAppsSymLink();

                    if (steamCmdProcess != null)
                    {
                        try
                        {
                            if (!steamCmdProcess.HasExited)
                            {
                                steamCmdProcess.Kill();
                                steamCmdProcess.WaitForExit(10000);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileId}", profileId);
                        }
                        finally
                        {
                            steamCmdProcess.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD: {ex.Message}");
                success = false;
            }

            return success;
        }

        #endregion

        #region Helper Methods

        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các process trước khi tắt ứng dụng...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các process trước khi tắt ứng dụng...");

            await StopAllProfilesAsync();

            _scheduleTimer.Stop();
            _scheduleTimer.Dispose();
        }

        public async Task<bool> RestartProfileAsync(int profileId)
        {
            await StopAndCleanupProfileAsync(profileId);

            await Task.Delay(2000);

            return await StartProfileAsync(profileId);
        }

        public async Task StartAllAutoRunProfilesAsync()
        {
            var profiles = await _profileService.GetAllProfiles();
            var autoRunProfiles = profiles.Where(p => p.AutoRun).ToList();

            if (!autoRunProfiles.Any())
            {
                _logger.LogInformation("Không có profile nào được đánh dấu Auto Run");
                return;
            }

            _logger.LogInformation("Đang khởi động {Count} profile Auto Run", autoRunProfiles.Count);
            await SafeSendLogAsync("System", "Info", $"Đang khởi động {autoRunProfiles.Count} profile Auto Run");

            foreach (var profile in autoRunProfiles)
            {
                try
                {
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang tự động khởi động {profile.Name}...");

                    await StopAndCleanupProfileAsync(profile.Id);

                    await StartProfileAsync(profile.Id);

                    await Task.Delay(3000);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tự động khởi động profile {ProfileName}: {Message}",
                        profile.Name, ex.Message);
                    await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi tự động khởi động: {ex.Message}");
                }
            }

            await SafeSendLogAsync("System", "Success", "Hoàn tất khởi động các profile Auto Run");
        }

        public List<LogEntry> GetLogs()
        {
            lock (_logs)
            {
                return new List<LogEntry>(_logs);
            }
        }

        public void ClearOldLogs(int retainCount = 1000)
        {
            lock (_logs)
            {
                if (_logs.Count > retainCount)
                {
                    _logs.RemoveRange(0, _logs.Count - retainCount);
                }
            }
        }

        #endregion
    }
}
