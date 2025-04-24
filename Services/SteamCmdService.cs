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
using SteamCmdWebAPI.Helpers;
using SteamCmdWebAPI.Extensions;

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
        private const int ProcessExitTimeoutMs = 10000;

        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private readonly System.Timers.Timer _scheduleTimer;

        private volatile bool _isRunningAllProfiles = false;
        private int _currentProfileIndex = 0;
        private volatile bool _cancelAutoRun = false;
        private DateTime _lastAutoRunTime = DateTime.MinValue;

        private readonly List<LogEntry> _logs = new List<LogEntry>(MaxLogEntries);
        private HashSet<string> _recentLogMessages = new HashSet<string>();
        private readonly int _maxRecentLogMessages = 100;

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
            _logger.LogInformation("Bộ lập lịch đã khởi động.");
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
                string logKey = $"{profileName}:{status}:{message}";
                if (_recentLogMessages.Contains(logKey))
                {
                    return;
                }
                _recentLogMessages.Add(logKey);
                if (_recentLogMessages.Count > _maxRecentLogMessages)
                {
                    _recentLogMessages.Clear();
                }
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

        private async Task<bool> KillProcessAsync(Process process, string profileName)
        {
            if (process == null || process.HasExited)
                return true;

            try
            {
                _logger.LogInformation("Đang dừng tiến trình SteamCMD cho profile {ProfileName}, PID: {Pid}",
                    profileName, process.Id);

                await SafeSendLogAsync(profileName, "Info", $"Đang dừng tiến trình SteamCMD (PID: {process.Id})");

                process.Terminator(ProcessExitTimeoutMs);

                await SafeSendLogAsync(profileName, "Info", $"Đã dừng tiến trình SteamCMD (PID: {process.Id})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileName}", profileName);

                try
                {
                    CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe", 5000);
                    return true;
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
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    await KillProcessAsync(kvp.Value, $"Profile {kvp.Key}");
                    _steamCmdProcesses.TryRemove(kvp.Key, out _);
                }

                var processes = Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")).ToList();

                if (processes.Any())
                {
                    _logger.LogInformation("Đang dừng {Count} tiến trình SteamCMD...", processes.Count);

                    foreach (var p in processes)
                    {
                        try
                        {
                            if (!p.HasExited)
                            {
                                p.Terminator(5000);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD, PID: {Pid}", p.Id);
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
                        CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe", 5000);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả tiến trình SteamCMD");
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

        public async Task<bool> IsSteamCmdInstalled()
        {
            // Không chỉ kiểm tra sự tồn tại của file mà còn xác minh rằng nó hoạt động đúng
            bool installed = await VerifySteamCmdInstallationAsync();
            _logger.LogInformation("Kiểm tra SteamCMD: {Installed}", installed ? "Đã cài đặt và hoạt động" : "Chưa cài đặt hoặc không hoạt động");
            return installed;
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

                try { File.Delete(zipPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip"); }

                // Kiểm tra SteamCMD bằng cách chạy và xác minh "Loading Steam API...OK"
                bool isInstalled = await VerifySteamCmdInstallationAsync();

                if (!isInstalled)
                {
                    throw new Exception("Cài đặt thất bại. SteamCMD không thể khởi động đúng cách.");
                }

                await SafeSendLogAsync("System", "Info", "Đã cài đặt SteamCMD thành công");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình cài đặt SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi cài đặt SteamCMD: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> VerifySteamCmdInstallationAsync()
        {
            string steamCmdPath = GetSteamCmdPath();
            if (!File.Exists(steamCmdPath))
            {
                return false;
            }

            try
            {
                // Tạo và cấu hình process SteamCMD để kiểm tra
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = steamCmdPath,
                        Arguments = "+quit",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(steamCmdPath)
                    },
                    EnableRaisingEvents = true
                };

                bool steamApiOk = false;
                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data) && e.Data.Contains("Loading Steam API...OK"))
                    {
                        steamApiOk = true;
                    }
                };

                process.Start();
                process.BeginOutputReadLine();

                // Đặt timeout 30 giây
                bool exited = process.WaitForExit(30000);

                if (!exited)
                {
                    process.Kill();
                    return false;
                }

                return steamApiOk;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xác minh cài đặt SteamCMD");
                return false;
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

        #region Folder Setup

        private async Task PrepareFolderStructure(string gameInstallDir)
        {
            try
            {
                string steamappsDir = Path.Combine(gameInstallDir, "steamapps");
                string localSteamappsDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

                if (!Directory.Exists(steamappsDir))
                {
                    Directory.CreateDirectory(steamappsDir);
                }

                if (Directory.Exists(localSteamappsDir))
                {
                    try
                    {
                        CmdHelper.RunCommand($"rmdir /S /Q \"{localSteamappsDir}\"", 5000);
                        await Task.Delay(1000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể xóa thư mục steamapps cũ");
                    }
                }

                CmdHelper.RunCommand($"mklink /D \"{localSteamappsDir}\" \"{steamappsDir}\"", 5000);
                await Task.Delay(1000);

                if (!Directory.Exists(localSteamappsDir))
                {
                    Directory.CreateDirectory(localSteamappsDir);
                }
            }
            catch (Exception)
            {
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

                if (_steamCmdProcesses.TryGetValue(id, out var existingProcess))
                {
                    await KillProcessAsync(existingProcess, profile.Name);
                    _steamCmdProcesses.TryRemove(id, out _);
                }

                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs);

                profile.Status = "Running";
                profile.StartTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);

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

                await PrepareFolderStructure(profile.InstallDirectory);

                bool success = await RunSteamCmdProcessAsync(profile, id);

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
            var successMessages = new HashSet<string>();

            try
            {
                if (!File.Exists(steamCmdPath))
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại tại {steamCmdPath}");
                    return false;
                }

                string logFilePath = GetSteamCmdLogPath(profileId);

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
                            StandardErrorEncoding = Encoding.UTF8,
                            WorkingDirectory = Path.GetDirectoryName(steamCmdPath)
                        },
                        EnableRaisingEvents = true
                    };

                    var outputBuffer = new StringBuilder();
                    var lastMessageHash = new HashSet<string>();
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
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"{output}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                            duplicateCount = 0;
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", output);
                        }
                    };
                    outputTimer.Start();

                    steamCmdProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data))
                            return;

                        if (e.Data.Contains($"Success! App '{profile.AppID}' already up to date") ||
                            e.Data.Contains($"Success! App '{profile.AppID}' fully installed") ||
                            e.Data.Contains($"Success! App '{profile.AppID}' updated"))
                        {
                            successMessages.Add(e.Data);
                        }

                        if (lastMessageHash.Contains(e.Data))
                        {
                            duplicateCount++;
                            return;
                        }

                        lastMessageHash.Add(e.Data);

                        if (lastMessageHash.Count > 50)
                        {
                            lastMessageHash.Clear();
                        }

                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(e.Data);
                        }
                    };

                    steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data))
                            return;

                        if (!lastMessageHash.Contains(e.Data))
                        {
                            _logger.LogError("SteamCMD Error: {Data}", e.Data);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi: {e.Data}");
                            lastMessageHash.Add(e.Data);
                        }
                    };

                    _steamCmdProcesses[profileId] = steamCmdProcess;

                    steamCmdProcess.Start();

                    profile.Pid = steamCmdProcess.Id;
                    await _profileService.UpdateProfile(profile);

                    steamCmdProcess.BeginOutputReadLine();
                    steamCmdProcess.BeginErrorReadLine();

                    steamCmdProcess.WaitForExit();

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
                            await _hubContext.Clients.All.SendAsync("ReceiveLog",
                                $"{remainingOutput}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", remainingOutput);
                        }
                    }

                    if (successMessages.Count > 0)
                    {
                        success = true;
                        string exitMessage = $"Cập nhật thành công game {profile.AppID}";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Success", exitMessage));
                    }
                    else
                    {
                        string exitMessage = $"Quá trình cập nhật game {profile.AppID} không thành công";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Error", exitMessage));
                    }
                }
                finally
                {
                    _logFileReader.StopMonitoring();

                    _steamCmdProcesses.TryRemove(profileId, out _);

                    if (steamCmdProcess != null && !steamCmdProcess.HasExited)
                    {
                        try
                        {
                            steamCmdProcess.Terminator(ProcessExitTimeoutMs);
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

                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs * 2);

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

            foreach (var profileId in _steamCmdProcesses.Keys.ToList())
            {
                if (_steamCmdProcesses.TryRemove(profileId, out var process))
                {
                    await KillProcessAsync(process, $"Profile {profileId}");
                    process.Dispose();
                }
            }

            await KillAllSteamCmdProcessesAsync();

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
            if (_steamCmdProcesses.TryRemove(profileId, out var process))
            {
                await KillProcessAsync(process, $"Profile {profileId}");
                process.Dispose();
            }

            await Task.Delay(RetryDelayMs * 2);

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
        #endregion
    }
}