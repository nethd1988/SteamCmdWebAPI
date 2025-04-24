using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;
using System.Net.Http;

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
        private const int RetryDelayMs = 2000;
        private const int MaxRetries = 3;
        private const int ProcessExitTimeoutMs = 10000;

        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
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

        #region Log and Notification Methods

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

        #region Schedule and Auto Run

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

            await KillAllSteamCmdProcessesAsync();
            await RemoveSteamAppsSymLinkAsync();
            await Task.Delay(RetryDelayMs);

            foreach (var profile in autoRunProfiles)
            {
                try
                {
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang tự động khởi động {profile.Name}...");
                    await RunProfileAsync(profile.Id);
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

        #region Process Management Utilities

        private void RunCommand(string command, int timeoutMs = 0)
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = new Process { StartInfo = processInfo })
            {
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.LogInformation(e.Data);
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        _logger.LogError(e.Data);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (timeoutMs > 0)
                {
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                    }
                }
                else
                {
                    process.WaitForExit();
                }
            }
        }

        private bool IsSymbolicLink(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return false;

                var attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> KillProcessAsync(Process process, string profileName)
        {
            if (process == null || process.HasExited)
                return true;

            try
            {
                _logger.LogInformation("Đang kill tiến trình SteamCMD cho profile {ProfileName}, PID: {Pid}",
                    profileName, process.Id);

                await SafeSendLogAsync(profileName, "Info", $"Đang kill tiến trình SteamCMD (PID: {process.Id})");

                process.Kill(true);
                bool exited = process.WaitForExit(ProcessExitTimeoutMs);

                if (!exited)
                {
                    _logger.LogWarning("Tiến trình không thoát sau {Timeout}ms, thử taskkill...", ProcessExitTimeoutMs);
                    RunCommand($"taskkill /PID {process.Id} /F", 5000);
                    exited = process.WaitForExit(ProcessExitTimeoutMs);
                }

                if (!exited)
                {
                    _logger.LogError("Không thể kill tiến trình SteamCMD cho profile {ProfileName}", profileName);
                    await SafeSendLogAsync(profileName, "Error", "Không thể kill tiến trình SteamCMD");
                    return false;
                }

                await SafeSendLogAsync(profileName, "Info", $"Đã kill tiến trình SteamCMD (PID: {process.Id})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi kill tiến trình SteamCMD cho profile {ProfileName}", profileName);

                try
                {
                    RunCommand("taskkill /F /IM steamcmd.exe", 5000);
                    return !process.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        private async Task<bool> KillAllSteamCmdProcessesAsync()
        {
            try
            {
                // Dừng tất cả các tiến trình đang theo dõi
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    await KillProcessAsync(kvp.Value, $"Profile {kvp.Key}");
                    _steamCmdProcesses.TryRemove(kvp.Key, out _);
                }

                // Kiểm tra và dừng các tiến trình SteamCMD còn lại
                var processes = Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")).ToList();

                if (processes.Any())
                {
                    _logger.LogInformation("Đang kill {Count} tiến trình SteamCMD...", processes.Count);
                    await SafeSendLogAsync("System", "Info", $"Đang kill {processes.Count} tiến trình SteamCMD...");

                    foreach (var p in processes)
                    {
                        try
                        {
                            if (!p.HasExited)
                            {
                                p.Kill(true);
                                p.WaitForExit(5000);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi khi kill tiến trình SteamCMD, PID: {Pid}", p.Id);
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }

                    await Task.Delay(2000);
                    processes = Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")).ToList();

                    if (processes.Any())
                    {
                        _logger.LogWarning("Vẫn còn {Count} tiến trình SteamCMD, thử taskkill...", processes.Count);
                        RunCommand("taskkill /F /IM steamcmd.exe", 5000);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kill tất cả tiến trình SteamCMD");
                return false;
            }
        }

        #endregion

        #region SteamCMD Installation and Path Management

        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            return Path.Combine(steamCmdDir, "steamcmd.exe");
        }

        public Task<bool> IsSteamCmdInstalled()
        {
            string steamCmdPath = GetSteamCmdPath();
            bool exists = File.Exists(steamCmdPath);

            _logger.LogInformation("Kiểm tra SteamCMD tại {Path}: {Exists}", steamCmdPath, exists);

            return Task.FromResult(exists);
        }

        public async Task InstallSteamCmd()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");
            string downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

            try
            {
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd: {Directory}", steamCmdDir);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamcmd: {steamCmdDir}");
                }

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);

                    await SafeSendLogAsync("System", "Info", $"Đang tải SteamCMD từ {downloadUrl}...");
                    _logger.LogInformation("Bắt đầu tải SteamCMD từ {Url}", downloadUrl);

                    var response = await httpClient.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();

                    using (var fs = new FileStream(zipPath, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }

                    await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                }

                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, steamCmdDir, true);
                await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công");

                try { File.Delete(zipPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip"); }

                if (!File.Exists(GetSteamCmdPath()))
                {
                    throw new Exception("Cài đặt thất bại. Không tìm thấy steamcmd.exe sau khi cài đặt.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình cài đặt SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi cài đặt SteamCMD: {ex.Message}");
                throw;
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

        #endregion

        #region SymLink Management

        private async Task<bool> CreateSteamAppsSymLink(string gameInstallDir)
        {
            string gameSteamAppsPath = Path.Combine(gameInstallDir, "steamapps");
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            // Đảm bảo trước tiên là xóa symbolic link cũ nếu có
            await RemoveSteamAppsSymLinkAsync();
            await Task.Delay(RetryDelayMs);

            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    // Đảm bảo thư mục steamapps trong thư mục cài đặt game tồn tại
                    if (!Directory.Exists(gameSteamAppsPath))
                    {
                        Directory.CreateDirectory(gameSteamAppsPath);
                        await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamapps tại {gameSteamAppsPath}");
                    }

                    // Tạo symbolic link với quyền admin
                    ProcessStartInfo linkInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C mklink /D \"{localSteamAppsPath}\" \"{gameSteamAppsPath}\"",
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        Verb = "runas",
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    using (var process = Process.Start(linkInfo))
                    {
                        if (process == null)
                        {
                            throw new Exception("Không thể khởi động process để tạo symbolic link");
                        }

                        process.WaitForExit();

                        if (process.ExitCode != 0)
                        {
                            throw new Exception($"Lỗi tạo symbolic link, exit code: {process.ExitCode}");
                        }
                    }

                    // Đợi để đảm bảo symbolic link đã được tạo
                    await Task.Delay(RetryDelayMs);

                    // Kiểm tra xem symbolic link đã được tạo thành công chưa
                    if (!Directory.Exists(localSteamAppsPath))
                    {
                        throw new Exception("Symbolic link không được tạo thành công");
                    }

                    _logger.LogInformation("Đã tạo symbolic link từ {Source} đến {Target}", localSteamAppsPath, gameSteamAppsPath);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo symbolic link từ {localSteamAppsPath} đến {gameSteamAppsPath}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi tạo symbolic link, thử lại lần {Attempt}/{MaxRetries}", i + 1, MaxRetries);
                    await Task.Delay(RetryDelayMs);
                }
            }

            _logger.LogError("Không thể tạo symbolic link sau {MaxRetries} lần thử", MaxRetries);
            await SafeSendLogAsync("System", "Error", $"Không thể tạo symbolic link sau {MaxRetries} lần thử");
            return false;
        }

        private async Task<bool> RemoveSteamAppsSymLinkAsync()
        {
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            if (!Directory.Exists(localSteamAppsPath))
                return true;

            for (int i = 0; i < MaxRetries; i++)
            {
                try
                {
                    _logger.LogInformation("Đang xóa thư mục steamapps tại {Path}", localSteamAppsPath);

                    if (IsSymbolicLink(localSteamAppsPath))
                    {
                        // Sử dụng cmd để xóa symbolic link
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/C rmdir \"{localSteamAppsPath}\"",
                            UseShellExecute = true,
                            CreateNoWindow = true,
                            Verb = "runas",
                            WindowStyle = ProcessWindowStyle.Hidden
                        };

                        using (var process = Process.Start(psi))
                        {
                            if (process != null)
                            {
                                process.WaitForExit();
                                if (process.ExitCode != 0)
                                {
                                    throw new Exception($"Không thể xóa symbolic link, exit code: {process.ExitCode}");
                                }
                            }
                        }
                    }
                    else
                    {
                        // Nếu không phải symbolic link, xóa thư mục thường
                        Directory.Delete(localSteamAppsPath, true);
                    }

                    // Đợi một khoảng thời gian để đảm bảo xóa thành công
                    await Task.Delay(RetryDelayMs);

                    if (!Directory.Exists(localSteamAppsPath))
                    {
                        _logger.LogInformation("Đã xóa thư mục steamapps tại {Path}", localSteamAppsPath);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi xóa thư mục steamapps, thử lại lần {Attempt}/{MaxRetries}", i + 1, MaxRetries);
                    await Task.Delay(RetryDelayMs);
                }
            }

            // Thử phương pháp cuối cùng
            try
            {
                _logger.LogWarning("Không thể xóa thư mục steamapps bằng phương thức thông thường, thử cmd /C rmdir /S /Q...");
                RunCommand($"rmdir /S /Q \"{localSteamAppsPath}\"", 5000);

                await Task.Delay(RetryDelayMs);
                return !Directory.Exists(localSteamAppsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Không thể xóa thư mục steamapps sau tất cả các nỗ lực");
                return false;
            }
        }

        #endregion

        #region Public API Methods

        public async Task<bool> RunProfileAsync(int id)
        {
            try
            {
                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    _logger.LogError("Không tìm thấy profile ID {ProfileId}", id);
                    await SafeSendLogAsync($"Profile {id}", "Error", $"Không tìm thấy profile với ID {id}");
                    return false;
                }

                await SafeSendLogAsync(profile.Name, "Info", $"Đang chuẩn bị chạy {profile.Name}...");

                // Bước 1: Dọn dẹp môi trường trước khi chạy
                if (_steamCmdProcesses.TryGetValue(id, out var existingProcess))
                {
                    await KillProcessAsync(existingProcess, profile.Name);
                    _steamCmdProcesses.TryRemove(id, out _);
                }

                await KillAllSteamCmdProcessesAsync();
                await RemoveSteamAppsSymLinkAsync();
                await Task.Delay(RetryDelayMs);

                // Cập nhật trạng thái profile
                profile.Status = "Running";
                profile.StartTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);

                // Bước 2: Kiểm tra SteamCMD đã cài đặt chưa
                if (!await IsSteamCmdInstalled())
                {
                    await SafeSendLogAsync(profile.Name, "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                    await InstallSteamCmd();

                    if (!await IsSteamCmdInstalled())
                    {
                        await SafeSendLogAsync(profile.Name, "Error", "Không thể cài đặt SteamCMD");

                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        return false;
                    }
                }

                // Bước 3: Kiểm tra và chuẩn bị thư mục cài đặt
                if (!string.IsNullOrEmpty(profile.InstallDirectory))
                {
                    if (!Directory.Exists(profile.InstallDirectory))
                    {
                        try
                        {
                            Directory.CreateDirectory(profile.InstallDirectory);
                            await SafeSendLogAsync(profile.Name, "Info", $"Đã tạo thư mục cài đặt: {profile.InstallDirectory}");
                        }
                        catch (Exception ex)
                        {
                            await SafeSendLogAsync(profile.Name, "Error", $"Không thể tạo thư mục cài đặt: {ex.Message}");

                            profile.Status = "Stopped";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                            return false;
                        }
                    }

                    // Kiểm tra quyền ghi
                    try
                    {
                        string testFilePath = Path.Combine(profile.InstallDirectory, "writetest.tmp");
                        File.WriteAllText(testFilePath, "test");
                        File.Delete(testFilePath);
                    }
                    catch (Exception ex)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"Không có quyền ghi vào thư mục cài đặt: {ex.Message}");

                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        return false;
                    }
                }

                // Bước 4: Tạo symbolic link và chạy SteamCMD
                if (!await CreateSteamAppsSymLink(profile.InstallDirectory))
                {
                    await SafeSendLogAsync(profile.Name, "Error", "Không thể tạo symbolic link. Quá trình không thể tiếp tục.");

                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    await _profileService.UpdateProfile(profile);
                    return false;
                }

                bool success = await RunSteamCmdProcessAsync(profile, id);

                // Cập nhật trạng thái profile sau khi chạy
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
                _logger.LogError(ex, "Lỗi khi chạy profile {ProfileId}: {Message}", id, ex.Message);

                try
                {
                    var profile = await _profileService.GetProfileById(id);
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

                _steamCmdProcesses.TryRemove(id, out _);
                return false;
            }
        }

        private async Task<bool> RunSteamCmdProcessAsync(SteamCmdProfile profile, int profileId)
        {
            Process steamCmdProcess = null;
            bool success = false;
            string steamCmdPath = GetSteamCmdPath();

            try
            {
                if (!File.Exists(steamCmdPath))
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại tại {steamCmdPath}");
                    return false;
                }

                string logFilePath = GetSteamCmdLogPath(profileId);

                // Khởi động LogFileReader
                _logFileReader.StartMonitoring(logFilePath, async (content) =>
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", content);
                    }
                });

                try
                {
                    // Chuẩn bị lệnh đăng nhập
                    string loginCommand = "+login anonymous";

                    if (!string.IsNullOrEmpty(profile.SteamUsername) && !string.IsNullOrEmpty(profile.SteamPassword))
                    {
                        try
                        {
                            string username = _encryptionService.Decrypt(profile.SteamUsername);
                            string password = _encryptionService.Decrypt(profile.SteamPassword);
                            loginCommand = $"+login {username} {password}";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã thông tin đăng nhập cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã thông tin đăng nhập: {ex.Message}");
                        }
                    }

                    // Chuẩn bị các tham số
                    string validateArg = profile.ValidateFiles ? "validate" : "";
                    string argumentsArg = string.IsNullOrEmpty(profile.Arguments) ? "" : profile.Arguments.Trim();

                    // Chuẩn bị câu lệnh đầy đủ
                    string arguments = $"{loginCommand} +app_update {profile.AppID} {validateArg} {argumentsArg} +quit".Trim();

                    // Ẩn thông tin đăng nhập trong log
                    string safeArguments = arguments.Contains("+login ") ?
                        arguments.Replace("+login ", "+login [credentials] ") :
                        arguments;

                    _logger.LogInformation("Chạy SteamCMD với tham số: {SafeArguments}", safeArguments);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy SteamCMD cho {profile.Name}...");

                    // Tạo và cấu hình process SteamCMD
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
                            StandardErrorEncoding = Encoding.UTF8,
                            WorkingDirectory = Path.GetDirectoryName(steamCmdPath)
                        },
                        EnableRaisingEvents = true
                    };

                    // Tạo buffer và timer để gửi output theo lô
                    var outputBuffer = new StringBuilder();
                    var lastMessageHash = new HashSet<string>(); // Để theo dõi thông báo trùng lặp
                    var duplicateCount = 0;
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

                        if (duplicateCount > 0)
                        {
                            // Nếu có thông báo trùng lặp, thêm thông tin về số lượng
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"{output}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                            duplicateCount = 0;
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", output);
                        }
                    };
                    outputTimer.Start();

                    // Xử lý output
                    steamCmdProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data))
                            return;

                        // Kiểm tra tin nhắn trùng lặp
                        if (lastMessageHash.Contains(e.Data))
                        {
                            // Tăng bộ đếm tin nhắn trùng lặp
                            duplicateCount++;
                            return;
                        }

                        // Thêm vào danh sách các tin nhắn đã xử lý
                        lastMessageHash.Add(e.Data);

                        // Giới hạn kích thước của HashSet để tránh tràn bộ nhớ
                        if (lastMessageHash.Count > 50)
                        {
                            lastMessageHash.Clear();
                        }

                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(e.Data);
                        }
                    };

                    // Xử lý lỗi
                    steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data))
                            return;

                        // Kiểm tra xem lỗi này đã được ghi lại chưa
                        if (!lastMessageHash.Contains(e.Data))
                        {
                            _logger.LogError("SteamCMD Error: {Data}", e.Data);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi: {e.Data}");

                            // Thêm vào danh sách để tránh trùng lặp
                            lastMessageHash.Add(e.Data);
                        }
                    };

                    // Lưu tiến trình vào dictionary
                    _steamCmdProcesses[profileId] = steamCmdProcess;

                    // Khởi động tiến trình
                    steamCmdProcess.Start();

                    // Cập nhật thông tin profile
                    profile.Pid = steamCmdProcess.Id;
                    await _profileService.UpdateProfile(profile);

                    // Bắt đầu đọc output và error
                    steamCmdProcess.BeginOutputReadLine();
                    steamCmdProcess.BeginErrorReadLine();

                    // Chờ tiến trình kết thúc
                    steamCmdProcess.WaitForExit();

                    // Dừng timer và xử lý output còn lại
                    outputTimer.Stop();
                    string remainingOutput;
                    lock (outputBuffer)
                    {
                        remainingOutput = outputBuffer.ToString();
                        outputBuffer.Clear();
                    }

                    if (!string.IsNullOrEmpty(remainingOutput))
                    {
                        if (duplicateCount > 0)
                        {
                            // Hiển thị thông tin về tin nhắn trùng lặp
                            await _hubContext.Clients.All.SendAsync("ReceiveLog",
                                $"{remainingOutput}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", remainingOutput);
                        }
                    }

                    // Lấy mã thoát và ghi log
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
                    // Dừng giám sát log
                    _logFileReader.StopMonitoring();

                    // Xóa tiến trình khỏi dictionary
                    _steamCmdProcesses.TryRemove(profileId, out _);

                    // Đóng process nếu vẫn đang chạy
                    if (steamCmdProcess != null && !steamCmdProcess.HasExited)
                    {
                        try
                        {
                            steamCmdProcess.Kill(true);
                            steamCmdProcess.WaitForExit(ProcessExitTimeoutMs);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD: {Message}", ex.Message);
                        }
                    }

                    steamCmdProcess?.Dispose();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD: {ex.Message}");
                success = false;

                steamCmdProcess?.Dispose();
            }

            return success;
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
                    await SafeSendLogAsync("System", "Warning", "Không có cấu hình nào để chạy");
                    return;
                }

                _isRunningAllProfiles = true;
                _cancelAutoRun = false;
                _currentProfileIndex = 0;

                await SafeSendLogAsync("System", "Info", "Bắt đầu chạy tất cả các profile...");

                // Đảm bảo môi trường sạch trước khi bắt đầu
                await KillAllSteamCmdProcessesAsync();
                await RemoveSteamAppsSymLinkAsync();

                // Đợi để đảm bảo tài nguyên được giải phóng
                await Task.Delay(RetryDelayMs * 2);

                // Chạy từng profile
                foreach (var profile in profiles)
                {
                    if (_cancelAutoRun) break;

                    _currentProfileIndex++;

                    await SafeSendLogAsync("System", "Info",
                        $"Đang chuẩn bị chạy profile ({_currentProfileIndex}/{profiles.Count}): {profile.Name}");

                    try
                    {
                        bool success = await RunProfileAsync(profile.Id);

                        if (!success)
                        {
                            await SafeSendLogAsync(profile.Name, "Warning",
                                $"Chạy profile {profile.Name} không thành công");
                        }

                        // Đợi giữa các lần chạy
                        if (_currentProfileIndex < profiles.Count)
                        {
                            await Task.Delay(3000);
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

        public async Task StopAllProfilesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các profiles...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profiles...");

            _cancelAutoRun = true;

            // Dừng tất cả các tiến trình đang chạy
            foreach (var profileId in _steamCmdProcesses.Keys.ToList())
            {
                if (_steamCmdProcesses.TryRemove(profileId, out var process))
                {
                    await KillProcessAsync(process, $"Profile {profileId}");
                    process.Dispose();
                }
            }

            // Đảm bảo không có tiến trình nào còn chạy
            await KillAllSteamCmdProcessesAsync();

            // Xóa symbolic link
            await RemoveSteamAppsSymLinkAsync();

            // Cập nhật trạng thái tất cả các profile
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

        public async Task<bool> RestartProfileAsync(int profileId)
        {
            // Dừng profile
            if (_steamCmdProcesses.TryRemove(profileId, out var process))
            {
                await KillProcessAsync(process, $"Profile {profileId}");
                process.Dispose();
            }

            await Task.Delay(RetryDelayMs * 2);

            // Chạy lại profile
            return await RunProfileAsync(profileId);
        }

        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các process trước khi tắt ứng dụng...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các process trước khi tắt ứng dụng...");

            _scheduleTimer.Stop();
            _scheduleTimer.Dispose();

            await StopAllProfilesAsync();
        }
    }
}
#endregion