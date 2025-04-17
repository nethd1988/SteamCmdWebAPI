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

namespace SteamCmdWebAPI.Services
{
    public class SteamCmdService
    {
        private readonly ILogger<SteamCmdService> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly ProfileService _profileService;
        private readonly SettingsService _settingsService;
        private readonly EncryptionService _encryptionService;

        private const int MaxLogEntries = 5000;

        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _profileSemaphore = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _keepAliveCtsDict = new ConcurrentDictionary<int, CancellationTokenSource>();
        private readonly ConcurrentDictionary<int, bool> _twoFactorRequestedDict = new ConcurrentDictionary<int, bool>();

        private readonly System.Timers.Timer _scheduleTimer;
        private volatile bool _isRunningAllProfiles = false;
        private int _currentProfileIndex = 0;
        private volatile bool _cancelAutoRun = false;

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
            EncryptionService encryptionService)
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _settingsService = settingsService;
            _encryptionService = encryptionService;

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
                if (now.Hour == settings.ScheduledHour && now.Minute == 0)
                {
                    _logger.LogInformation("Đang chạy tất cả profile theo lịch hẹn tại {0}h...", settings.ScheduledHour);
                    await RunAllProfilesAsync();
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
                if (_steamCmdProcesses.TryRemove(profileId, out var existingProcess))
                {
                    try
                    {
                        if (existingProcess != null && !existingProcess.HasExited)
                        {
                            existingProcess.Kill();
                            existingProcess.WaitForExit(10000);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cũ cho profile {ProfileId}", profileId);
                    }
                    finally
                    {
                        existingProcess?.Dispose();
                    }
                }

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogError("Không tìm thấy profile ID {ProfileId}", profileId);
                    await SafeSendLogAsync($"Profile {profileId}", "Error", $"Không tìm thấy profile với ID {profileId}");
                    return false;
                }

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
                }

                if (!await IsSteamCmdInstalled())
                {
                    string errorMsg = "Không thể cài đặt hoặc tìm thấy SteamCMD";
                    _logger.LogError(errorMsg);
                    await SafeSendLogAsync(profile.Name, "Error", errorMsg);

                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    await _profileService.UpdateProfile(profile);
                    return false;
                }

                if (!string.IsNullOrEmpty(profile.InstallDirectory) && !Directory.Exists(profile.InstallDirectory))
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

                await RunSteamCmdAsync(GetSteamCmdPath(), profile, profileId);

                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                profile.LastRun = DateTime.UtcNow;
                await _profileService.UpdateProfile(profile);

                await SafeSendLogAsync(profile.Name, "Success", $"Đã chạy {profile.Name} thành công");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy profile {ProfileId}: {Message}", profileId, ex.Message);

                var profile = await _profileService.GetProfileById(profileId);
                if (profile != null)
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy {profile.Name}: {ex.Message}");
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0;
                    await _profileService.UpdateProfile(profile);
                }

                _steamCmdProcesses.TryRemove(profileId, out _);
                _keepAliveCtsDict.TryRemove(profileId, out _);
                _twoFactorRequestedDict.TryRemove(profileId, out _);
                return false;
            }
        }

        public async Task StopAllProfilesAsync()
        {
            try
            {
                _logger.LogInformation("Đang dừng tất cả các profiles...");
                await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profiles...");

                _cancelAutoRun = true;

                // Kiểm tra các profile đang chờ 2FA
                foreach (var kvp in _twoFactorRequestedDict)
                {
                    if (kvp.Value)
                    {
                        await SafeSendLogAsync($"Profile {kvp.Key}", "Warning", "Profile đang chờ mã 2FA, hủy yêu cầu 2FA...");
                        await _hubContext.Clients.All.SendAsync("CancelTwoFactorRequest", kvp.Key);
                    }
                }

                KillAllSteamCmdProcesses();

                var profiles = await _profileService.GetAllProfiles();
                foreach (var profile in profiles.Where(p => p.Status == "Running"))
                {
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0;
                    await _profileService.UpdateProfile(profile);
                }

                await SafeSendLogAsync("System", "Success", "Đã dừng tất cả các profiles");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả các profiles: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi dừng tất cả các profiles: {ex.Message}");
                throw new Exception($"Lỗi khi dừng tất cả các profiles: {ex.Message}");
            }
        }

        private void KillAllSteamCmdProcesses()
        {
            try
            {
                foreach (var kvp in _steamCmdProcesses)
                {
                    var process = kvp.Value;
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            process.Kill();
                            if (!process.WaitForExit(10000))
                            {
                                _logger.LogWarning("Tiến trình không kết thúc sau 10s cho profile {0}", kvp.Key);
                            }
                            _logger.LogInformation("Đã kill tiến trình steamcmd với PID {0} cho profile {1}", process.Id, kvp.Key);
                            _ = SafeSendLogAsync("System", "Info", $"Đã kill tiến trình steamcmd với PID {process.Id} cho profile {kvp.Key}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi khi kill tiến trình steamcmd với PID {0} cho profile {1}",
                            process?.Id ?? 0, kvp.Key);
                    }
                    finally
                    {
                        process?.Dispose();
                        _keepAliveCtsDict.TryRemove(kvp.Key, out var cts);
                        cts?.Cancel();
                        _twoFactorRequestedDict.TryRemove(kvp.Key, out _);
                    }
                }

                _steamCmdProcesses.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kill tất cả tiến trình steamcmd: {Message}", ex.Message);
                throw new Exception($"Lỗi khi kill tất cả tiến trình steamcmd: {ex.Message}");
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
                    await SafeSendLogAsync($"Profile {id}", "Error", $"Không tìm thấy cấu hình với ID {id}");
                    _logger.LogWarning("Không tìm thấy cấu hình với ID {Id}", id);
                    return false;
                }

                return await StartProfileAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD cho profile ID {Id}", id);
                await SafeSendLogAsync($"Profile {id}", "Error", $"Lỗi: {ex.Message}");
                KillAllSteamCmdProcesses();
                throw;
            }
        }

        public async Task RunAllProfilesAsync()
        {
            await _profileSemaphore.WaitAsync();
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

                while (_currentProfileIndex < profiles.Count && !_cancelAutoRun)
                {
                    var profile = profiles[_currentProfileIndex];
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy profile ({_currentProfileIndex + 1}/{profiles.Count}): {profile.Name}");
                    await StartProfileAsync(profile.Id);
                    _currentProfileIndex++;
                    await Task.Delay(2000);
                }

                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
                await SafeSendLogAsync("System", "Success", "Đã hoàn thành chạy tất cả các profile");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tất cả các profile");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chạy tất cả các profile: {ex.Message}");
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
            }
            finally
            {
                _profileSemaphore.Release();
            }
        }

        private async Task RunNextProfileAsync()
        {
            if (!_isRunningAllProfiles || _cancelAutoRun)
            {
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
                return;
            }

            var profiles = await _profileService.GetAllProfiles();
            if (_currentProfileIndex >= profiles.Count)
            {
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
                await SafeSendLogAsync("System", "Success", "Đã hoàn thành chạy tất cả các profile");
                return;
            }

            var profile = profiles[_currentProfileIndex];
            await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy profile ({_currentProfileIndex + 1}/{profiles.Count}): {profile.Name}");
            await StartProfileAsync(profile.Id);
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

            _logger.LogInformation("SteamCMD path: {Path}", path);

            return path;
        }

        private async Task CreateSteamAppsSymLink(string gameInstallDir)
        {
            string gameSteamAppsPath = Path.Combine(gameInstallDir, "steamapps");
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            try
            {
                if (!Directory.Exists(gameSteamAppsPath))
                {
                    Directory.CreateDirectory(gameSteamAppsPath);
                    _logger.LogInformation("Đã tạo thư mục steamapps tại {Path}", gameSteamAppsPath);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamapps tại {gameSteamAppsPath}");
                }

                if (Directory.Exists(localSteamAppsPath))
                {
                    try
                    {
                        if (IsSymbolicLink(localSteamAppsPath))
                        {
                            DeleteSymbolicLink(localSteamAppsPath);
                            _logger.LogInformation("Đã xóa symbolic link tại {Path}", localSteamAppsPath);
                        }
                        else
                        {
                            Directory.Delete(localSteamAppsPath, true);
                            _logger.LogInformation("Đã xóa thư mục steamapps cục bộ tại {Path}", localSteamAppsPath);
                        }

                        await SafeSendLogAsync("System", "Info", $"Đã xóa thư mục/symlink steamapps tại {localSteamAppsPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể xóa {Path}, sẽ thử phương pháp thay thế", localSteamAppsPath);
                        try
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                RunProcessWithTimeout("cmd.exe", $"/C rmdir \"{localSteamAppsPath}\"", 10000);
                            }
                            else
                            {
                                RunProcessWithTimeout("rm", $"-f \"{localSteamAppsPath}\"", 10000);
                            }
                        }
                        catch (Exception innerEx)
                        {
                            _logger.LogError(innerEx, "Không thể xóa symlink bằng phương pháp thay thế");
                        }
                    }
                }

                _logger.LogInformation("Tạo symbolic link từ {Source} đến {Target}", gameSteamAppsPath, localSteamAppsPath);

                bool success = false;

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        if (Directory.CreateSymbolicLink(localSteamAppsPath, gameSteamAppsPath) != null)
                        {
                            success = true;
                            _logger.LogInformation("Đã tạo symbolic link từ {Source} đến {Target} bằng API .NET", gameSteamAppsPath, localSteamAppsPath);
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
                        _logger.LogInformation("Đã tạo symbolic link từ {Source} đến {Target} bằng process", gameSteamAppsPath, localSteamAppsPath);
                    }
                    else
                    {
                        _logger.LogError("Không thể tạo symbolic link sau nhiều lần thử");
                        throw new Exception("Không thể tạo symbolic link sau nhiều lần thử");
                    }
                }

                await SafeSendLogAsync("System", "Info", $"Đã tạo symbolic link từ {gameSteamAppsPath} đến {localSteamAppsPath}");
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
                            DeleteSymbolicLink(localSteamAppsPath);
                        }
                        else
                        {
                            Directory.Delete(localSteamAppsPath, false);
                        }
                        _logger.LogInformation("Đã xóa symbolic link tại {Path}", localSteamAppsPath);
                        await SafeSendLogAsync("System", "Info", $"Đã xóa symbolic link tại {localSteamAppsPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể xóa symbolic link theo cách thông thường, thử phương pháp thay thế");
                        try
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                RunProcessWithTimeout("cmd.exe", $"/C rmdir \"{localSteamAppsPath}\"", 10000);
                            }
                            else
                            {
                                RunProcessWithTimeout("rm", $"-f \"{localSteamAppsPath}\"", 10000);
                            }
                            await SafeSendLogAsync("System", "Info", $"Đã xóa symbolic link tại {localSteamAppsPath}");
                        }
                        catch (Exception innerEx)
                        {
                            _logger.LogError(innerEx, "Không thể xóa symbolic link bằng phương pháp thay thế");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa symbolic link tại {Path}: {Message}", localSteamAppsPath, ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi xóa symbolic link tại {localSteamAppsPath}: {ex.Message}");
            }
        }

        private async Task RunSteamCmdAsync(string steamCmdPath, SteamCmdProfile profile, int profileId)
        {
            if (string.IsNullOrEmpty(steamCmdPath))
                throw new ArgumentNullException(nameof(steamCmdPath));
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrEmpty(profile.InstallDirectory))
                throw new ArgumentNullException(nameof(profile.InstallDirectory));
            if (string.IsNullOrEmpty(profile.Name))
                throw new ArgumentNullException(nameof(profile.Name));

            if (!File.Exists(steamCmdPath))
            {
                _logger.LogError("File SteamCMD không tồn tại tại {Path}", steamCmdPath);
                throw new FileNotFoundException($"File SteamCMD không tồn tại tại {steamCmdPath}");
            }

            await CreateSteamAppsSymLink(profile.InstallDirectory);

            Process steamCmdProcess = null;
            TaskCompletionSource<bool> twoFactorTcs = null;
            bool twoFactorRequested = false;
            CancellationTokenSource keepAliveCts = new CancellationTokenSource();
            _keepAliveCtsDict.TryAdd(profileId, keepAliveCts);

            try
            {
                string loginCommand;
                string usernameToUse = "anonymous";
                string passwordToUse = "";

                if (profile.AnonymousLogin)
                {
                    loginCommand = "+login anonymous";
                }
                else
                {
                    if (!string.IsNullOrEmpty(profile.SteamUsername))
                    {
                        try
                        {
                            usernameToUse = _encryptionService.Decrypt(profile.SteamUsername);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã SteamUsername cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã tên đăng nhập: {ex.Message}");
                            usernameToUse = "anonymous";
                        }
                    }

                    if (!string.IsNullOrEmpty(profile.SteamPassword))
                    {
                        try
                        {
                            passwordToUse = _encryptionService.Decrypt(profile.SteamPassword);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã SteamPassword cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã mật khẩu: {ex.Message}");
                            passwordToUse = "";
                        }
                    }

                    loginCommand = string.IsNullOrEmpty(passwordToUse)
                        ? $"+login {usernameToUse}"
                        : $"+login {usernameToUse} {passwordToUse}";
                }

                string validateArg = profile.ValidateFiles ? "validate" : "";
                string argumentsArg = string.IsNullOrEmpty(profile.Arguments) ? "" : profile.Arguments.Trim();
                string arguments = $"{loginCommand} +app_update {profile.AppID} {validateArg} {argumentsArg} +quit".Trim();

                string safeArguments = !string.IsNullOrEmpty(passwordToUse) && !profile.AnonymousLogin
                    ? arguments.Replace(passwordToUse, "********")
                    : arguments;

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
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                SemaphoreSlim twoFactorSemaphore = new SemaphoreSlim(0, 1);

                steamCmdProcess.OutputDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && !steamCmdProcess.HasExited)
                    {
                        _logger.LogInformation("SteamCMD Output: {Data}", e.Data);
                        await SafeSendLogAsync(profile.Name, "Info", e.Data);

                        string trimmedData = e.Data.Trim().ToLower();
                        if (trimmedData.Contains("enter the current code from your mobile authenticator") ||
                            trimmedData.Contains("waiting for user to enter steam guard code from email") ||
                            trimmedData.Contains("rate limit exceeded") ||
                            trimmedData.Contains("this computer has not been authenticated") ||
                            trimmedData.Contains("steam guard code:"))
                        {
                            twoFactorRequested = true;
                            _twoFactorRequestedDict.TryAdd(profileId, true);
                            twoFactorTcs = new TaskCompletionSource<bool>();
                            await ProcessTwoFactorRequest(profileId, profile.Name, steamCmdProcess, twoFactorTcs);
                            twoFactorSemaphore.Release();
                        }
                    }
                };

                steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogError("SteamCMD Error: {Data}", e.Data);
                        await SafeSendLogAsync(profile.Name, "Error", e.Data);

                        string trimmedData = e.Data.Trim().ToLower();
                        if (trimmedData.Contains("rate limit exceeded") ||
                            trimmedData.Contains("this computer has not been authenticated") ||
                            trimmedData.Contains("steam guard code:"))
                        {
                            twoFactorRequested = true;
                            _twoFactorRequestedDict.TryAdd(profileId, true);
                            twoFactorTcs = new TaskCompletionSource<bool>();
                            await ProcessTwoFactorRequest(profileId, profile.Name, steamCmdProcess, twoFactorTcs);
                            twoFactorSemaphore.Release();
                        }
                    }
                };

                _steamCmdProcesses.TryAdd(profileId, steamCmdProcess);

                steamCmdProcess.Start();

                profile.Pid = steamCmdProcess.Id;
                await _profileService.UpdateProfile(profile);

                steamCmdProcess.BeginOutputReadLine();
                steamCmdProcess.BeginErrorReadLine();

                var keepAliveTask = Task.Run(async () =>
                {
                    while (!keepAliveCts.Token.IsCancellationRequested && !steamCmdProcess.HasExited)
                    {
                        await Task.Delay(10000, keepAliveCts.Token);
                        if (!steamCmdProcess.HasExited)
                        {
                            try
                            {
                                await steamCmdProcess.StandardInput.WriteLineAsync();
                                await steamCmdProcess.StandardInput.FlushAsync();
                                _logger.LogInformation("Gửi tín hiệu giữ kết nối với SteamCMD");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Lỗi khi gửi tín hiệu giữ kết nối");
                            }
                        }
                    }
                }, keepAliveCts.Token);

                if (await Task.WhenAny(twoFactorSemaphore.WaitAsync(), steamCmdProcess.WaitForExitAsync()) == twoFactorSemaphore.WaitAsync())
                {
                    await twoFactorTcs.Task;
                    await steamCmdProcess.WaitForExitAsync();
                }

                keepAliveCts.Cancel();
                await keepAliveTask;

                _logger.LogInformation("SteamCMD process đã kết thúc với exit code: {ExitCode}", steamCmdProcess.ExitCode);
                await SafeSendLogAsync(profile.Name, steamCmdProcess.ExitCode == 0 ? "Success" : "Error",
                    $"SteamCMD đã kết thúc với mã: {steamCmdProcess.ExitCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD: {ex.Message}");
                throw;
            }
            finally
            {
                keepAliveCts?.Cancel();
                _keepAliveCtsDict.TryRemove(profileId, out _);
                _twoFactorRequestedDict.TryRemove(profileId, out _);
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

        private async Task ProcessTwoFactorRequest(int profileId, string profileName, Process steamCmdProcess, TaskCompletionSource<bool> tcs)
        {
            _logger.LogInformation("Phát hiện yêu cầu 2FA, gửi yêu cầu popup");

            try
            {
                await SafeSendLogAsync(profileName, "Warning", "Steam Guard code: Vui lòng nhập mã xác thực");
                await _hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);

                string twoFactorCode = await LogHub.RequestTwoFactorCode(profileId, _hubContext);

                if (!string.IsNullOrEmpty(twoFactorCode))
                {
                    await SafeSendLogAsync(profileName, "Info", "Đã nhận mã 2FA, đang tiếp tục...");
                    await steamCmdProcess.StandardInput.WriteLineAsync(twoFactorCode);
                    await steamCmdProcess.StandardInput.FlushAsync();
                    _logger.LogInformation("Đã gửi mã 2FA cho Steam Guard: {Code}", twoFactorCode);
                    tcs.SetResult(true);
                }
                else
                {
                    _logger.LogWarning("Không nhận được mã 2FA, quá trình có thể thất bại");
                    await SafeSendLogAsync(profileName, "Warning", "Không nhận được mã 2FA, quá trình có thể thất bại");
                    tcs.SetResult(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý yêu cầu 2FA");
                await SafeSendLogAsync(profileName, "Error", $"Lỗi khi xử lý 2FA: {ex.Message}");
                tcs.SetResult(false);
            }
            finally
            {
                _twoFactorRequestedDict.TryRemove(profileId, out _);
            }
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

        #region Helper Classes and Methods

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
            if (_steamCmdProcesses.TryGetValue(profileId, out var process))
            {
                try
                {
                    if (process != null && !process.HasExited)
                    {
                        process.Kill();
                        process.WaitForExit(10000);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileId}", profileId);
                }
                finally
                {
                    process?.Dispose();
                    _steamCmdProcesses.TryRemove(profileId, out _);
                    _keepAliveCtsDict.TryRemove(profileId, out var cts);
                    cts?.Cancel();
                    _twoFactorRequestedDict.TryRemove(profileId, out _);
                }
            }

            var profile = await _profileService.GetProfileById(profileId);
            if (profile != null)
            {
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);
            }

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

        #endregion
    }
}