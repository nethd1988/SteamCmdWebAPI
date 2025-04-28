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
using Newtonsoft.Json;
using System.Text.RegularExpressions;

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
        private readonly SteamApiService _steamApiService;
        private readonly DependencyManagerService _dependencyManagerService; // Added based on 1.txt point 5

        private const int MaxLogEntries = 5000;
        private const int RetryDelayMs = 5000;
        private const int ProcessExitTimeoutMs = 20000;

        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private readonly System.Timers.Timer _scheduleTimer;

        private volatile bool _isRunningAllProfiles = false;
        private int _currentProfileIndex = 0;
        private volatile bool _cancelAutoRun = false;
        private DateTime _lastAutoRunTime = DateTime.MinValue;

        private readonly List<LogEntry> _logs = new List<LogEntry>(MaxLogEntries);
        private HashSet<string> _recentLogMessages = new HashSet<string>();
        private readonly int _maxRecentLogMessages = 100;

        private readonly ConcurrentDictionary<string, DateTime> _recentHubMessages = new ConcurrentDictionary<string, DateTime>();
        private readonly System.Timers.Timer _hubMessageCleanupTimer;
        private const int HubMessageCacheDurationSeconds = 5;
        private const int HubMessageCleanupIntervalMs = 10000;

        private volatile bool _lastRunHadLoginError = false;


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

        private readonly ConcurrentQueue<int> _updateQueue = new ConcurrentQueue<int>();
        private volatile bool _isProcessingQueue = false;
        private Task _queueProcessorTask = null;

        public SteamCmdService(
    ILogger<SteamCmdService> logger,
    IHubContext<LogHub> hubContext,
    ProfileService profileService,
    SettingsService settingsService,
    EncryptionService encryptionService,
    LogFileReader logFileReader,
    SteamApiService steamApiService,
    DependencyManagerService dependencyManagerService)
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _settingsService = settingsService;
            _encryptionService = encryptionService;
            _logFileReader = logFileReader;
            _steamApiService = steamApiService;
            _dependencyManagerService = dependencyManagerService; // Added based on 1.txt point 5

            _scheduleTimer = new System.Timers.Timer(60000);
            _scheduleTimer.Elapsed += async (s, e) => await CheckScheduleAsync();
            _scheduleTimer.AutoReset = true;
            _scheduleTimer.Start();
            _logger.LogInformation("Bộ lập lịch đã khởi động.");

            _hubMessageCleanupTimer = new System.Timers.Timer(HubMessageCleanupIntervalMs);
            _hubMessageCleanupTimer.Elapsed += (s, e) => CleanupRecentHubMessages();
            _hubMessageCleanupTimer.AutoReset = true;
            _hubMessageCleanupTimer.Start();
            _logger.LogInformation("Bộ lập lịch dọn dẹp log hub đã khởi động.");
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

                if (!_recentHubMessages.TryGetValue(message, out var lastSentTime) || (DateTime.Now - lastSentTime).TotalSeconds > HubMessageCacheDurationSeconds)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", message);
                    _recentHubMessages[message] = DateTime.Now;
                }

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

        private void CleanupRecentHubMessages()
        {
            var cutoffTime = DateTime.Now.AddSeconds(-HubMessageCacheDurationSeconds);
            foreach (var entry in _recentHubMessages.Where(kvp => kvp.Value < cutoffTime).ToList())
            {
                _recentHubMessages.TryRemove(entry.Key, out _);
            }
        }
        #endregion

        #region Schedule and Auto Run
        public async Task StartAllAutoRunProfilesAsync()
        {
            var profiles = await _profileService.GetAllProfiles();
            var autoRunProfiles = profiles.Where(p => p.AutoRun).ToList();

            if (!autoRunProfiles.Any())
            {
                _logger.LogInformation("Không có profile nào được đánh dấu Auto Run");
                return;
            }

            _logger.LogInformation("Đang thêm {Count} profile Auto Run vào hàng đợi", autoRunProfiles.Count);
            await SafeSendLogAsync("System", "Info", $"Đang thêm {autoRunProfiles.Count} profile Auto Run vào hàng đợi cập nhật");

            await KillAllSteamCmdProcessesAsync();

            foreach (var profile in autoRunProfiles)
            {
                await QueueProfileForUpdate(profile.Id);
                await Task.Delay(500);
            }

            await SafeSendLogAsync("System", "Success", "Đã thêm tất cả profile Auto Run vào hàng đợi cập nhật");
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
                    _logger.LogInformation("Đang thêm tất cả profile vào hàng đợi theo khoảng thời gian {0} giờ", intervalHours);
                    await StartAllAutoRunProfilesAsync();
                    _lastAutoRunTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra lịch hẹn");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi kiểm tra lịch hẹn: {ex.Message}");
            }
        }
        #endregion

        #region Process Management Utilities
        private async Task<bool> KillProcessAsync(Process process, string profileName)
        {
            if (process == null || process.HasExited)
                return true;
            try
            {
                _logger.LogInformation("Đang dừng tiến trình SteamCMD cho {ProfileName} (PID: {PID})", profileName, process.Id);
                process.Terminator(ProcessExitTimeoutMs);
                await Task.Delay(500);
                _logger.LogInformation("Đã dừng tiến trình SteamCMD cho {ProfileName}", profileName);
                return process.HasExited;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho {ProfileName} (PID: {PID})", profileName, process?.Id);
                return false;
            }
        }

        private async Task<bool> KillAllSteamCmdProcessesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các tiến trình SteamCMD...");
            bool success = true;
            try
            {
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    if (!await KillProcessAsync(kvp.Value, $"Profile {kvp.Key}"))
                    {
                        success = false;
                    }
                    _steamCmdProcesses.TryRemove(kvp.Key, out _);
                }

                _logger.LogInformation("Sử dụng taskkill để đảm bảo tất cả steamcmd.exe đã dừng...");
                CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe /T", ProcessExitTimeoutMs);
                await Task.Delay(1500);

                _logger.LogInformation("Đã hoàn tất dừng các tiến trình SteamCMD.");
                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả tiến trình SteamCMD");
                return false;
            }
        }
        #endregion

        #region SteamCmd Installation and Path Management
        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            return Path.Combine(steamCmdDir, "steamcmd.exe");
        }

        public Task<bool> IsSteamCmdInstalled()
        {
            return Task.FromResult(File.Exists(GetSteamCmdPath()));
        }

        public async Task InstallSteamCmd()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");
            string steamCmdPath = GetSteamCmdPath();
            string downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

            try
            {
                await SafeSendLogAsync("System", "Info", "Bắt đầu quá trình cài đặt SteamCMD...");
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs);

                if (File.Exists(steamCmdPath))
                {
                    try
                    {
                        File.Delete(steamCmdPath);
                        await Task.Delay(1000);
                    }
                    catch (IOException ioEx)
                    {
                        _logger.LogWarning(ioEx, "Không thể xóa steamcmd.exe hiện tại, có thể đang bị khóa. Thử kill lại và xóa.");
                        await SafeSendLogAsync("System", "Warning", "Không thể xóa steamcmd.exe, thử kill lại...");
                        CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe /T", ProcessExitTimeoutMs);
                        await Task.Delay(2000);
                        try
                        {
                            File.Delete(steamCmdPath);
                            await Task.Delay(1000);
                        }
                        catch (Exception finalDeleteEx)
                        {
                            _logger.LogError(finalDeleteEx, "Xóa steamcmd.exe thất bại lần cuối.");
                            await SafeSendLogAsync("System", "Error", $"Không thể xóa steamcmd.exe hiện tại: {finalDeleteEx.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi không mong muốn khi xóa steamcmd.exe cũ.");
                        await SafeSendLogAsync("System", "Error", $"Lỗi khi xóa steamcmd.exe cũ: {ex.Message}");
                    }
                }


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

                    if (File.Exists(zipPath))
                    {
                        try { File.Delete(zipPath); } catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip cũ."); }
                    }

                    var response = await httpClient.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                    await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                }

                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(3000);

                try
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, steamCmdDir, true);
                    await SafeSendLogAsync("System", "Success", "Đã giải nén SteamCMD thành công.");
                }
                catch (IOException ioEx)
                {
                    _logger.LogError(ioEx, "Lỗi IO khi giải nén SteamCMD. Thử dùng PowerShell.");
                    await SafeSendLogAsync("System", "Error", $"Lỗi IO khi giải nén: {ioEx.Message}. Thử dùng PowerShell...");
                    CmdHelper.RunCommand($"powershell -command \"Expand-Archive -Path '{zipPath}' -DestinationPath '{steamCmdDir}' -Force\"", 60000);
                    await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi không mong muốn khi giải nén SteamCMD.");
                    await SafeSendLogAsync("System", "Error", $"Lỗi giải nén không mong muốn: {ex.Message}");
                    throw;
                }
                finally
                {
                    try { File.Delete(zipPath); } catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip sau khi giải nén."); }
                }


                if (!File.Exists(steamCmdPath))
                {
                    throw new Exception("Cài đặt thất bại. Không tìm thấy steamcmd.exe sau khi giải nén.");
                }

                try
                {
                    FileInfo fileInfo = new FileInfo(steamCmdPath);
                    if (fileInfo.Length < 10000)
                    {
                        throw new Exception("File steamcmd.exe được tạo nhưng có kích thước không hợp lệ (< 10KB).");
                    }
                    _logger.LogInformation("Đã cài đặt và xác thực SteamCMD thành công.");
                    await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công.");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra file steamcmd.exe sau cài đặt: {Message}", ex.Message);
                    await SafeSendLogAsync("System", "Error", $"Lỗi khi kiểm tra file steamcmd.exe: {ex.Message}");
                    throw;
                }
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "Lỗi mạng khi tải SteamCMD từ {Url}", downloadUrl);
                await SafeSendLogAsync("System", "Error", $"Lỗi mạng khi tải SteamCMD: {httpEx.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình cài đặt SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi nghiêm trọng khi cài đặt SteamCMD: {ex.Message}");
                throw;
            }
        }


        private string GetSteamCmdLogPath(int profileId)
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string logsDir = Path.Combine(steamCmdDir, "logs");
            Directory.CreateDirectory(logsDir);
            return Path.Combine(logsDir, $"console_log_{profileId}.txt");
        }
        #endregion

        #region Folder Setup
        private async Task<bool> PrepareFolderStructure(string gameInstallDir)
        {
            if (string.IsNullOrWhiteSpace(gameInstallDir))
            {
                _logger.LogError("Thư mục cài đặt không hợp lệ hoặc bị bỏ trống.");
                await SafeSendLogAsync("System", "Error", "Thư mục cài đặt không hợp lệ hoặc bị bỏ trống.");
                return false;
            }

            string steamappsTargetDir = Path.Combine(gameInstallDir, "steamapps");
            string localSteamappsLinkDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            if (!Directory.Exists(steamappsTargetDir))
            {
                try
                {
                    Directory.CreateDirectory(steamappsTargetDir);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục đích: {steamappsTargetDir}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Không thể tạo thư mục đích: {steamappsTargetDir}");
                    await SafeSendLogAsync("System", "Error", $"Không thể tạo thư mục đích: {steamappsTargetDir}: {ex.Message}");
                    return false;
                }
            }

            if (Directory.Exists(localSteamappsLinkDir) || File.Exists(localSteamappsLinkDir))
            {
                bool deleted = false;
                int maxRetries = 10;
                int currentRetryDelay = 3000;

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        if ((File.GetAttributes(localSteamappsLinkDir) & FileAttributes.Directory) == FileAttributes.Directory)
                        {
                            CmdHelper.RunCommand($"rmdir /S /Q \"{localSteamappsLinkDir}\"", 45000);
                        }
                        else
                        {
                            CmdHelper.RunCommand($"del /F /Q \"{localSteamappsLinkDir}\"", 15000);
                        }

                        await Task.Delay(1000);

                        if (!Directory.Exists(localSteamappsLinkDir) && !File.Exists(localSteamappsLinkDir))
                        {
                            deleted = true;
                            break;
                        }
                        else
                        {
                            await SafeSendLogAsync("System", "Warning", $"Thư mục/liên kết steamapps cũ vẫn tồn tại (thử {i + 1}). Đang dừng SteamCMD...");
                            await KillAllSteamCmdProcessesAsync();
                            await Task.Delay(currentRetryDelay);
                            currentRetryDelay += 2000;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Lỗi khi xóa thư mục/liên kết steamapps cục bộ cũ lần thử {i + 1}");
                        await SafeSendLogAsync("System", "Error", $"Lỗi khi xóa (thử {i + 1}): {ex.Message}");
                        await KillAllSteamCmdProcessesAsync();
                        await Task.Delay(currentRetryDelay);
                        currentRetryDelay += 2000;
                    }
                }

                if (!deleted)
                {
                    _logger.LogError($"Không thể xóa thư mục/liên kết steamapps cục bộ cũ tại {localSteamappsLinkDir} sau {maxRetries} lần thử.");
                    await SafeSendLogAsync("System", "Error", $"Không thể xóa {localSteamappsLinkDir} sau {maxRetries} lần thử. Không thể tạo liên kết.");
                    return false;
                }
            }

            if (!Directory.Exists(localSteamappsLinkDir))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(localSteamappsLinkDir));

                    CmdHelper.RunCommand($"mklink /D \"{localSteamappsLinkDir}\" \"{steamappsTargetDir}\"", 15000);
                    await Task.Delay(2000);

                    if (Directory.Exists(localSteamappsLinkDir))
                    {
                        return true;
                    }
                    else
                    {
                        _logger.LogError($"Không thể tạo liên kết tượng trưng đến {localSteamappsLinkDir}. Thư mục không tồn tại sau lệnh mklink.");
                        await SafeSendLogAsync("System", "Error", $"Không thể tạo liên kết tượng trưng đến {localSteamappsLinkDir}.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tạo liên kết tượng trưng");
                    await SafeSendLogAsync("System", "Error", $"Lỗi khi tạo liên kết tượng trưng: {ex.Message}");
                    return false;
                }
            }
            else
            {
                _logger.LogError("Thư mục/liên kết steamapps cục bộ vẫn tồn tại mặc dù đã cố gắng xóa. Bỏ qua tạo liên kết.");
                await SafeSendLogAsync("System", "Error", "Thư mục/liên kết steamapps cục bộ vẫn tồn tại. Bỏ qua tạo liên kết.");
                return false;
            }
        }
        #endregion

        #region Public API Methods (Queueing and Execution)

        public async Task<bool> QueueProfileForUpdate(int profileId)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("QueueProfileForUpdate: Không tìm thấy profile ID {ProfileId} để thêm vào hàng đợi", profileId);
                    return false;
                }

                // Kiểm tra xem đã có trong hàng đợi chưa
                bool alreadyInQueue = false;
                foreach (var id in _updateQueue)
                {
                    if (id == profileId)
                    {
                        alreadyInQueue = true;
                        break;
                    }
                }

                if (alreadyInQueue)
                {
                    _logger.LogInformation("QueueProfileForUpdate: Profile ID {ProfileId} ('{ProfileName}') đã có trong hàng đợi cập nhật",
                        profileId, profile.Name);
                    await SafeSendLogAsync(profile.Name, "Info", $"Profile {profile.Name} đã có trong hàng đợi.");
                    return true;
                }

                // Thêm vào hàng đợi
                _updateQueue.Enqueue(profileId);
                _logger.LogInformation("QueueProfileForUpdate: Đã thêm profile ID {ProfileId} ('{ProfileName}') vào hàng đợi với _isRunningAllProfiles = {Flag}",
                    profileId, profile.Name, _isRunningAllProfiles);
                await SafeSendLogAsync(profile.Name, "Info", $"Đã thêm Profile {profile.Name} vào hàng đợi cập nhật.");

                // Khởi động bộ xử lý hàng đợi nếu chưa chạy
                StartQueueProcessorIfNotRunning();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "QueueProfileForUpdate: Lỗi khi thêm profile ID {ProfileId} vào hàng đợi cập nhật", profileId);
                await SafeSendLogAsync($"Profile {profileId}", "Error", $"Lỗi khi thêm vào hàng đợi: {ex.Message}");
                return false;
            }
        }

        private void StartQueueProcessorIfNotRunning()
        {
            lock (_queueProcessorTask ?? new object())
            {
                if (_queueProcessorTask == null || _queueProcessorTask.IsCompleted)
                {
                    _logger.LogInformation("Khởi động bộ xử lý hàng đợi...");
                    _queueProcessorTask = Task.Run(ProcessUpdateQueueAsync);
                }
                else
                {
                    _logger.LogInformation("Bộ xử lý hàng đợi đã đang chạy...");
                }
            }
        }


        private async Task ProcessUpdateQueueAsync()
        {
            if (_isProcessingQueue)
            {
                _logger.LogWarning("ProcessUpdateQueueAsync called but already running. Exiting.");
                return;
            }
            _isProcessingQueue = true;
            _logger.LogInformation("ProcessUpdateQueueAsync: Set _isProcessingQueue = true");

            try
            {
                _logger.LogInformation("ProcessUpdateQueueAsync: Bắt đầu xử lý hàng đợi (Queue Count: {Count})", _updateQueue.Count);
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Bắt đầu xử lý hàng đợi cập nhật (Số lượng: {_updateQueue.Count})");

                while (_updateQueue.TryDequeue(out int profileId))
                {
                    var profile = await _profileService.GetProfileById(profileId);
                    string profileName = profile?.Name ?? $"Profile {profileId}";
                    _logger.LogInformation("ProcessUpdateQueueAsync: Dequeued {ProfileName} (ID: {ProfileId})", profileName, profileId);

                    _lastRunHadLoginError = false;

                    try
                    {
                        _logger.LogInformation("ProcessUpdateQueueAsync: Processing {ProfileName}...", profileName);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đang xử lý cập nhật cho {profileName} (ID: {profileId})...");

                        // Quan trọng: Đây là sự khác biệt chính
                        // Khi chạy từ RunAllProfilesAsync, _isRunningAllProfiles = true
                        // nên sẽ chạy tất cả app (chính và phụ thuộc)
                        bool success = await ExecuteProfileUpdateAsync(profileId, null);

                        if (success)
                        {
                            _logger.LogInformation("Đã xử lý cập nhật thành công cho {ProfileName} (ID: {ProfileId})", profileName, profileId);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cập nhật thành công cho {profileName} (ID: {profileId})");
                        }
                        else
                        {
                            _logger.LogWarning("Xử lý cập nhật không thành công cho {ProfileName} (ID: {ProfileId})", profileName, profileId);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cập nhật KHÔNG thành công cho {profileName} (ID: {profileId}). Kiểm tra log.");

                            if (_lastRunHadLoginError && !_isRunningAllProfiles)
                            {
                                _logger.LogWarning("Phát hiện lỗi đăng nhập khi chạy 1 profile. Dừng xử lý hàng đợi.");
                                await SafeSendLogAsync("System", "Warning", "Đã phát hiện lỗi đăng nhập hoặc lỗi thiếu thông tin đăng nhập. Dừng xử lý hàng đợi.");
                                break;
                            }
                        }

                        _logger.LogInformation("ProcessUpdateQueueAsync: Waiting 3000ms before next item...");
                        await Task.Delay(3000);
                        _logger.LogInformation("ProcessUpdateQueueAsync: Finished wait.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý cập nhật cho {ProfileName} (ID: {ProfileId}) từ hàng đợi", profileName, profileId);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi xử lý cập nhật cho {profileName} (ID: {profileId}): {ex.Message}");
                        if (profile != null)
                        {
                            profile.Status = "Error";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                        }
                        if (_lastRunHadLoginError && !_isRunningAllProfiles)
                        {
                            _logger.LogWarning("Phát hiện lỗi đăng nhập (hoặc lỗi sau đăng nhập) khi chạy 1 profile. Dừng xử lý hàng đợi.");
                            await SafeSendLogAsync("System", "Warning", "Đã phát hiện lỗi đăng nhập hoặc lỗi liên quan. Dừng xử lý hàng đợi.");
                            break;
                        }
                    }
                    _logger.LogInformation("ProcessUpdateQueueAsync: End of loop iteration for {ProfileName}. Remaining queue count: {Count}", profileName, _updateQueue.Count);
                }

                _logger.LogInformation("ProcessUpdateQueueAsync: Queue is now empty.");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã xử lý xong hàng đợi cập nhật");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình xử lý hàng đợi cập nhật");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi nghiêm trọng trong bộ xử lý hàng đợi: {ex.Message}");
            }
            finally
            {
                _isProcessingQueue = false;
                // Chỉ đặt lại _isRunningAllProfiles = false khi đã xử lý xong hàng đợi
                if (_isRunningAllProfiles)
                {
                    _logger.LogInformation("ProcessUpdateQueueAsync: Setting _isRunningAllProfiles = false");
                    _isRunningAllProfiles = false;
                }
            }
        }

        public async Task<bool> RunProfileAsync(int id)
        {
            // Khi chạy một profile riêng lẻ từ UI, chỉ chạy app ID chính
            var profile = await _profileService.GetProfileById(id);
            if (profile == null)
            {
                _logger.LogWarning("RunProfileAsync: Không tìm thấy profile ID {ProfileId} để chạy", id);
                return false;
            }

            _logger.LogInformation("RunProfileAsync: Chuẩn bị chạy profile '{ProfileName}' (ID: {ProfileId}) - chỉ AppID chính",
                profile.Name, id);

            // Đảm bảo _isRunningAllProfiles = false khi chạy riêng profile
            // Điều này sẽ đảm bảo chỉ app chính được chạy
            _isRunningAllProfiles = false;

            // Thêm profile vào hàng đợi để xử lý (ExecuteProfileUpdateAsync sẽ chạy app chính)
            return await QueueProfileForUpdate(id);
        }

        // Updated method signature to support specificAppId based on 1.txt point 1
        private async Task<bool> ExecuteProfileUpdateAsync(int id, string specificAppId = null, bool forceValidate = false)
        {
            var profile = await _profileService.GetProfileById(id);
            if (profile == null)
            {
                _logger.LogError("ExecuteProfileUpdateAsync: Profile ID {ProfileId} không tìm thấy", id);
                await SafeSendLogAsync($"Profile {id}", "Error", $"Profile ID {id} không tìm thấy để thực thi cập nhật");
                return false;
            }

            _logger.LogInformation("ExecuteProfileUpdateAsync: Bắt đầu cập nhật cho '{ProfileName}' (ID: {ProfileId}, specificAppId: {SpecificAppId}, _isRunningAllProfiles: {Flag})",
                profile.Name, id, specificAppId ?? "null", _isRunningAllProfiles);

            await SafeSendLogAsync(profile.Name, "Info", $"Chuẩn bị cập nhật '{profile.Name}' (AppID: {profile.AppID})...");

            await KillAllSteamCmdProcessesAsync();
            await Task.Delay(RetryDelayMs);

            if (!await IsSteamCmdInstalled())
            {
                await SafeSendLogAsync(profile.Name, "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                try
                {
                    await InstallSteamCmd();
                    if (!await IsSteamCmdInstalled())
                    {
                        throw new Exception("SteamCMD báo cài đặt thành công nhưng không tìm thấy file thực thi.");
                    }
                }
                catch (Exception installEx)
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"Không thể cài đặt SteamCMD: {installEx.Message}");
                    profile.Status = "Error";
                    profile.StopTime = DateTime.Now;
                    await _profileService.UpdateProfile(profile);
                    return false;
                }
            }

            if (string.IsNullOrWhiteSpace(profile.InstallDirectory))
            {
                await SafeSendLogAsync(profile.Name, "Error", "Thư mục cài đặt không được cấu hình cho profile này.");
                profile.Status = "Error";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);
                return false;
            }

            if (!Directory.Exists(profile.InstallDirectory))
            {
                try
                {
                    Directory.CreateDirectory(profile.InstallDirectory);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đã tạo thư mục cài đặt: {profile.InstallDirectory}");
                }
                catch (Exception ex)
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"Không thể tạo thư mục cài đặt '{profile.InstallDirectory}': {ex.Message}");
                    profile.Status = "Error";
                    profile.StopTime = DateTime.Now;
                    await _profileService.UpdateProfile(profile);
                    return false;
                }
            }

            string linkedSteamappsDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");
            if (!await PrepareFolderStructure(profile.InstallDirectory))
            {
                await SafeSendLogAsync(profile.Name, "Error", "Lỗi khi chuẩn bị cấu trúc thư mục.");
                profile.Status = "Error";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);
                return false;
            }

            // Xác định app ID cần cập nhật
            var appIdsToUpdate = new List<string>();
            try
            {
                // Trường hợp 1: Nếu có specificAppId - chỉ cập nhật app đó
                if (!string.IsNullOrEmpty(specificAppId))
                {
                    appIdsToUpdate.Add(specificAppId);
                    _logger.LogInformation("ExecuteProfileUpdateAsync: Chỉ cập nhật App ID cụ thể: {AppId}", specificAppId);
                    await SafeSendLogAsync(profile.Name, "Info", $"Chỉ cập nhật App ID: {specificAppId}");
                }
                // Trường hợp 2: Chạy từ nút "Chạy tất cả" (hoặc các trường hợp _isRunningAllProfiles = true) 
                // thì cập nhật tất cả app (chính và phụ thuộc)
                else if (_isRunningAllProfiles)
                {
                    // Đọc tất cả appmanifest_*.acf từ thư mục steamapps
                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                    if (Directory.Exists(steamappsDir))
                    {
                        var manifestFiles = Directory.GetFiles(steamappsDir, "appmanifest_*.acf");
                        var regex = new Regex(@"appmanifest_(\d+)\.acf");

                        _logger.LogInformation("ExecuteProfileUpdateAsync: Tìm thấy {Count} file manifest trong thư mục {Dir}",
                            manifestFiles.Length, steamappsDir);

                        foreach (var manifestFile in manifestFiles)
                        {
                            var match = regex.Match(Path.GetFileName(manifestFile));
                            if (match.Success)
                            {
                                string appId = match.Groups[1].Value;
                                if (!appIdsToUpdate.Contains(appId))
                                {
                                    appIdsToUpdate.Add(appId);
                                    _logger.LogInformation("ExecuteProfileUpdateAsync: Thêm App ID {AppId} từ file manifest", appId);
                                }
                            }
                        }

                        // Vẫn thêm ID chính nếu không có trong danh sách từ manifest
                        if (!appIdsToUpdate.Contains(profile.AppID))
                        {
                            appIdsToUpdate.Add(profile.AppID);
                            _logger.LogInformation("ExecuteProfileUpdateAsync: Thêm App ID chính {AppId} do không tìm thấy trong manifest",
                                profile.AppID);
                        }

                        var appIdsListForLog = appIdsToUpdate.Any() ? string.Join(", ", appIdsToUpdate) : "Không tìm thấy App ID nào ngoài ID chính";
                        _logger.LogInformation("ExecuteProfileUpdateAsync: Tổng số App ID để cập nhật: {Count} (từ các ID chính Và ID phụ): {AppIds}",
                           appIdsToUpdate.Count, appIdsListForLog);
                        await SafeSendLogAsync(profile.Name, "Info", $"Đã thu thập {appIdsToUpdate.Count} ứng dụng để cập nhật: [{appIdsListForLog}]");
                    }
                    else
                    {
                        // Thư mục steamapps không tồn tại, chỉ thêm app chính
                        appIdsToUpdate.Add(profile.AppID);
                        _logger.LogWarning("ExecuteProfileUpdateAsync: Thư mục steamapps không tồn tại {Dir}, chỉ cập nhật App ID {AppId}",
                            steamappsDir, profile.AppID);
                        await SafeSendLogAsync(profile.Name, "Warning", $"Thư mục steamapps không tồn tại tại {steamappsDir}. Chỉ cập nhật App ID: {profile.AppID}.");
                    }
                }
                // Trường hợp 3: Mặc định - chỉ chạy app chính
                else
                {
                    appIdsToUpdate.Add(profile.AppID);
                    _logger.LogInformation("ExecuteProfileUpdateAsync: Chỉ cập nhật App ID chính: {AppId} (Normal mode)", profile.AppID);
                    await SafeSendLogAsync(profile.Name, "Info", $"Chỉ cập nhật App ID : {profile.AppID}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExecuteProfileUpdateAsync: Lỗi khi xử lý App ID phụ thuộc cho profile {ProfileName}", profile.Name);
                await SafeSendLogAsync(profile.Name, "Warning", $"Lỗi khi xử lý App ID phụ thuộc: {ex.Message}. Sẽ chỉ cập nhật App ID.");

                // Đảm bảo ít nhất có app chính trong danh sách cập nhật
                if (!appIdsToUpdate.Contains(profile.AppID))
                {
                    appIdsToUpdate.Add(profile.AppID);
                }
            }

            // In log để debug
            _logger.LogInformation("ExecuteProfileUpdateAsync: Danh sách AppID cần cập nhật cho '{ProfileName}': {AppIds}",
                profile.Name, string.Join(", ", appIdsToUpdate));

            // Cập nhật trạng thái profile
            profile.Status = "Running";
            profile.StartTime = DateTime.Now;
            profile.Pid = 0;
            await _profileService.UpdateProfile(profile);

            // Lấy tên app để hiển thị trong log
            var appNamesForLog = new Dictionary<string, string>();
            foreach (var appId in appIdsToUpdate)
            {
                var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                appNamesForLog[appId] = appInfo?.Name ?? appId;
            }

            // Thực hiện cập nhật lần đầu (không verify)
            await SafeSendLogAsync(profile.Name, "Info", "═══════════════════════════════════════════════════════");
            await SafeSendLogAsync(profile.Name, "Info", "              LẦN CẬP NHẬT THỨ NHẤT (1)               ");
            await SafeSendLogAsync(profile.Name, "Info", "═══════════════════════════════════════════════════════");
            await SafeSendLogAsync(profile.Name, "Info", $"Bắt đầu lần cập nhật đầu tiên cho: {string.Join(", ", appNamesForLog.Select(kv => $"'{kv.Value}' ({kv.Key})"))}");
            await SafeSendLogAsync(profile.Name, "Info", "Chạy update KHÔNG Verify để kiểm tra nhanh");
            await SafeSendLogAsync(profile.Name, "Info", "═══════════════════════════════════════════════════════");

            var initialRunResult = await RunSteamCmdProcessAsync(profile, id, appIdsToUpdate, forceValidate: false);

            // Kiểm tra lỗi đăng nhập
            if (_lastRunHadLoginError)
            {
                _logger.LogError("ExecuteProfileUpdateAsync: Phát hiện lỗi đăng nhập sau khi RunSteamCmdProcessAsync hoàn tất");
                profile.Status = "Error";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);

                // Reset update flags khi có lỗi
                if (!string.IsNullOrEmpty(specificAppId))
                {
                    await _dependencyManagerService.ResetUpdateFlagsAsync(profile.Id, specificAppId);
                }
                else
                {
                    await _dependencyManagerService.ResetUpdateFlagsAsync(profile.Id);
                }

                return false;
            }

            // Kiểm tra kết quả chạy lần đầu
            if (!initialRunResult.Success && string.IsNullOrEmpty(specificAppId))
            {
                _logger.LogError("ExecuteProfileUpdateAsync: RunSteamCmdProcessAsync lần đầu tiên thất bại với Exit Code: {ExitCode}", initialRunResult.ExitCode);
                profile.Status = "Error";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);

                // Reset update flags
                await _dependencyManagerService.ResetUpdateFlagsAsync(profile.Id);
                return false;
            }

            // Khởi tạo biến cho lần chạy thứ hai
            bool retryRunSuccessful = true;
            var failedAppIdsForRetry = new List<string>();

            // Chỉ thực hiện kiểm tra manifest và chạy lại nếu không phải chạy một app cụ thể
            if (string.IsNullOrEmpty(specificAppId))
            {
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs);

                await SafeSendLogAsync(profile.Name, "Info", "Đang kiểm tra kết quả cập nhật lần đầu...");

                // Kiểm tra manifest của từng app sau lần chạy đầu
                foreach (var appId in appIdsToUpdate)
                {
                    var manifestData = await ReadAppManifest(linkedSteamappsDir, appId);
                    string gameName = appNamesForLog.TryGetValue(appId, out var name) ? name : appId;

                    if (manifestData == null)
                    {
                        _logger.LogWarning("ExecuteProfileUpdateAsync: Manifest cho '{GameName}' (AppID: {Appid}) không tìm thấy sau lần chạy đầu", gameName, appId);
                        await SafeSendLogAsync(profile.Name, "Warning", $"Manifest cho '{gameName}' ({appId}) không tìm thấy. Thử lại.");
                        failedAppIdsForRetry.Add(appId);
                    }
                    else if (!manifestData.TryGetValue("UpdateResult", out string updateResultValue) ||
                             (updateResultValue != "0" && updateResultValue != "2" && updateResultValue != "23"))
                    {
                        string resultText = manifestData.TryGetValue("UpdateResult", out var ur) ? $"UpdateResult: {ur}" : "UpdateResult không tồn tại";
                        _logger.LogWarning("ExecuteProfileUpdateAsync: Cập nhật lần đầu cho '{GameName}' không thành công: {Result}", gameName, resultText);
                        await SafeSendLogAsync(profile.Name, "Warning", $"Cập nhật '{gameName}' ({appId}) lần đầu không thành công ({resultText}). Thử lại.");
                        failedAppIdsForRetry.Add(appId);
                    }
                    else
                    {
                        _logger.LogInformation("ExecuteProfileUpdateAsync: Cập nhật lần đầu cho '{GameName}' thành công (UpdateResult: {Result})", gameName, updateResultValue);
                    }
                }

                // Lần chạy thứ hai nếu có app thất bại
                if (failedAppIdsForRetry.Any())
                {
                    await KillAllSteamCmdProcessesAsync();
                    await Task.Delay(RetryDelayMs);

                    var failedAppNamesForRetryLog = failedAppIdsForRetry
                        .Select(appId => $"'{appNamesForLog.GetValueOrDefault(appId, appId)}' ({appId})")
                        .ToList();

                    _logger.LogWarning("ExecuteProfileUpdateAsync: Phát hiện {Count} game cần thử lại với Verify", failedAppIdsForRetry.Count);
                    await SafeSendLogAsync(profile.Name, "Warning", $"Phát hiện {failedAppIdsForRetry.Count} game cần thử lại với Verify. Bắt đầu lần chạy thứ hai...");

                    // Chạy lại với validate
                    var retryRunResult = await RunSteamCmdProcessAsync(profile, id, failedAppIdsForRetry, forceValidate: true);

                    if (_lastRunHadLoginError)
                    {
                        _logger.LogError("ExecuteProfileUpdateAsync: Phát hiện lỗi đăng nhập trong lần chạy thứ hai");
                        profile.Status = "Error";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        await _dependencyManagerService.ResetUpdateFlagsAsync(profile.Id);
                        return false;
                    }

                    if (!retryRunResult.Success)
                    {
                        _logger.LogError("ExecuteProfileUpdateAsync: Lần chạy thứ hai thất bại với Exit Code: {ExitCode}", retryRunResult.ExitCode);
                        retryRunSuccessful = false;
                    }

                    // Kiểm tra lại manifest sau khi chạy lại
                    await KillAllSteamCmdProcessesAsync();
                    await Task.Delay(RetryDelayMs);

                    var failedAfterRetryCheck = new List<string>();
                    await SafeSendLogAsync(profile.Name, "Info", "Đang kiểm tra kết quả cuối cùng sau khi thử lại...");

                    foreach (var appId in failedAppIdsForRetry)
                    {
                        var manifestData = await ReadAppManifest(linkedSteamappsDir, appId);
                        string gameName = appNamesForLog.GetValueOrDefault(appId, appId);

                        if (manifestData == null)
                        {
                            _logger.LogError("ExecuteProfileUpdateAsync: Thử lại cho '{GameName}' thất bại, manifest vẫn không tìm thấy", gameName);
                            await SafeSendLogAsync(profile.Name, "Error", $"Thử lại cho '{gameName}' ({appId}) thất bại (không tìm thấy manifest).");
                            failedAfterRetryCheck.Add(appId);
                        }
                        else if (!manifestData.TryGetValue("UpdateResult", out string updateResultValue) ||
                                 (updateResultValue != "0" && updateResultValue != "2" && updateResultValue != "23"))
                        {
                            string resultText = manifestData.TryGetValue("UpdateResult", out var ur) ? $"UpdateResult: {ur}" : "UpdateResult không tồn tại";
                            _logger.LogError("ExecuteProfileUpdateAsync: Thử lại cho '{GameName}' thất bại ({Result})", gameName, resultText);
                            await SafeSendLogAsync(profile.Name, "Error", $"Thử lại cho '{gameName}' ({appId}) thất bại ({resultText}).");
                            failedAfterRetryCheck.Add(appId);
                        }
                        else
                        {
                            _logger.LogInformation("ExecuteProfileUpdateAsync: Thử lại cho '{GameName}' thành công (UpdateResult: {Result})", gameName, updateResultValue);
                            await SafeSendLogAsync(profile.Name, "Success", $"Thử lại cho '{gameName}' ({appId}) thành công (UpdateResult: {updateResultValue}).");
                        }
                    }

                    // Cập nhật danh sách app thất bại cuối cùng
                    failedAppIdsForRetry = failedAfterRetryCheck;
                }
            }

            // Đoạn cuối của phương thức ExecuteProfileUpdateAsync()
            // Xác định kết quả tổng thể
            bool overallSuccess;
            if (!string.IsNullOrEmpty(specificAppId))
            {
                // Nếu chạy một app cụ thể, kết quả dựa vào lần chạy đầu tiên
                overallSuccess = initialRunResult.Success;
            }
            else
            {
                // Nếu chạy app chính và phụ thuộc, kết quả dựa vào có app nào thất bại sau khi thử lại hay không
                overallSuccess = failedAppIdsForRetry.Count == 0;
            }

            // Cập nhật trạng thái profile
            profile.Status = overallSuccess ? "Stopped" : "Error";
            profile.StopTime = DateTime.Now;
            profile.Pid = 0;
            profile.LastRun = DateTime.Now;
            await _profileService.UpdateProfile(profile);

            // THÊM ĐOẠN CODE MỚI TẠI ĐÂY
            if (!string.IsNullOrEmpty(specificAppId))
            {
                // Lấy tên game
                var appInfo = await _steamApiService.GetAppUpdateInfo(specificAppId);
                string gameName = appInfo?.Name ?? $"AppID {specificAppId}";

                if (overallSuccess)
                {
                    _logger.LogInformation("ExecuteProfileUpdateAsync: Đã cập nhật thành công ứng dụng '{GameName}' (AppID: {AppId})",
                        gameName, specificAppId);
                    await SafeSendLogAsync(profile.Name, "Success", $"Cập nhật thành công '{gameName}' (AppID: {specificAppId}).");
                }
                else
                {
                    _logger.LogError("ExecuteProfileUpdateAsync: Cập nhật ứng dụng '{GameName}' (AppID: {AppId}) thất bại",
                        gameName, specificAppId);
                    await SafeSendLogAsync(profile.Name, "Error", $"Cập nhật '{gameName}' (AppID: {specificAppId}) thất bại.");
                }

                // Reset cờ cập nhật cho app cụ thể
                await _dependencyManagerService.ResetUpdateFlagsAsync(profile.Id, specificAppId);
            }
            else if (overallSuccess)
            {
                // Đây là code hiện tại của bạn cho case overallSuccess = true
                await SafeSendLogAsync(profile.Name, "Success", $"Hoàn tất cập nhật {profile.Name}.");
            }
            else
            {
                // Đây là code hiện tại của bạn cho case overallSuccess = false
                var finalFailedNames = new List<string>();
                if (!string.IsNullOrEmpty(specificAppId))
                {
                    if (!initialRunResult.Success)
                        finalFailedNames.Add(specificAppId);
                }
                else
                {
                    finalFailedNames = failedAppIdsForRetry;
                }

                var namesToLog = finalFailedNames
                                 .Select(appId => $"'{appNamesForLog.GetValueOrDefault(appId, appId)}' ({appId})")
                                 .ToList();

                string errorMsg = $"Cập nhật {profile.Name} không thành công.";
                if (namesToLog.Any())
                {
                    errorMsg += $" Các game sau có thể vẫn bị lỗi: {string.Join(", ", namesToLog)}.";
                }
                errorMsg += " Kiểm tra log chi tiết.";

                await SafeSendLogAsync(profile.Name, "Error", errorMsg);
            }
            // KẾT THÚC ĐOẠN CODE MỚI

            // Log kết quả cuối cùng
            _logger.LogInformation("ExecuteProfileUpdateAsync: Kết thúc cập nhật cho '{ProfileName}' (ID: {ProfileId}). Kết quả: {Result}",
                profile.Name, profile.Id, overallSuccess ? "Thành công" : "Thất bại");

            return overallSuccess;
        }




        private async Task<Dictionary<string, string>> GetAppNamesAsync(string steamappsDir, List<string> appIds)
        {
            var appNames = new Dictionary<string, string>();
            foreach (var appId in appIds)
            {
                string gameName = appId;
                var manifestData = await ReadAppManifest(steamappsDir, appId);
                if (manifestData != null && manifestData.TryGetValue("name", out var nameValue) && !string.IsNullOrWhiteSpace(nameValue))
                {
                    gameName = nameValue;
                }
                else
                {
                    var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                    if (appInfo != null && !string.IsNullOrEmpty(appInfo.Name))
                    {
                        gameName = appInfo.Name;
                    }
                    await Task.Delay(200);
                }
                appNames[appId] = gameName;
            }
            return appNames;
        }


        private class SteamCmdRunResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; } = -1;
        }

        private async Task<SteamCmdRunResult> RunSteamCmdProcessAsync(SteamCmdProfile profile, int profileId, List<string> appIdsToUpdate, bool forceValidate)
        {
            Process steamCmdProcess = null;
            var runResult = new SteamCmdRunResult { Success = false, ExitCode = -1 };
            string steamCmdPath = GetSteamCmdPath();
            string steamCmdDir = Path.GetDirectoryName(steamCmdPath);
            string loginCommand = null; // Chuyển biến ra ngoài khối try-catch

            _lastRunHadLoginError = false;

            // Các regex cho xử lý lỗi
            var invalidPasswordRegex = new Regex(@"^Logging in user '.*' \[U:1:\d+\] to Steam Public\.\.\.ERROR \(Invalid Password\)", RegexOptions.Compiled);
            var notOnlineErrorRegex = new Regex(@"^ERROR! Failed to request AppInfo update, not online or not logged in to Steam\.$", RegexOptions.Compiled);
            var errorAppRegex = new Regex(@"Error! App '(\d+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            // Danh sách app ID cần chạy lại với validate
            var appIdsToRetry = new HashSet<string>();

            try
            {
                if (!File.Exists(steamCmdPath))
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại: {steamCmdPath}");
                    runResult.ExitCode = -99;
                    return runResult;
                }

                if (!string.IsNullOrEmpty(profile.SteamUsername) && !string.IsNullOrEmpty(profile.SteamPassword))
                {
                    try
                    {
                        string username = _encryptionService.Decrypt(profile.SteamUsername);
                        string password = _encryptionService.Decrypt(profile.SteamPassword);
                        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                        {
                            _logger.LogError("Tên người dùng hoặc mật khẩu trống sau khi giải mã cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", "Lỗi: Tên người dùng hoặc mật khẩu trống sau khi giải mã.");
                            _lastRunHadLoginError = true;
                            runResult.ExitCode = -98;
                            return runResult;
                        }
                        loginCommand = $"+login {username} {password}";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi giải mã thông tin đăng nhập cho profile {ProfileName}", profile.Name);
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi giải mã thông tin đăng nhập: {ex.Message}. Không thể tiếp tục.");
                        _lastRunHadLoginError = true;
                        runResult.ExitCode = -97;
                        return runResult;
                    }
                }
                else
                {
                    _logger.LogError("Thông tin đăng nhập Steam (Username/Password) không được cung cấp cho profile {ProfileName}. Không thể đăng nhập.", profile.Name);
                    await SafeSendLogAsync(profile.Name, "Error", "Lỗi: Thông tin đăng nhập Steam không được cung cấp. Profile này yêu cầu tài khoản.");
                    _lastRunHadLoginError = true;
                    runResult.ExitCode = -96;
                    return runResult;
                }

                StringBuilder argumentsBuilder = new StringBuilder();
                argumentsBuilder.Append(loginCommand);

                if (appIdsToUpdate == null || !appIdsToUpdate.Any())
                {
                    _logger.LogWarning("RunSteamCmdProcessAsync được gọi không có App ID nào để cập nhật cho profile {ProfileName}", profile.Name);
                    await SafeSendLogAsync(profile.Name, "Warning", "Không có App ID nào được chỉ định để cập nhật trong lần chạy này.");
                    runResult.Success = true;
                    runResult.ExitCode = 0;
                    return runResult;
                }

                foreach (var appId in appIdsToUpdate)
                {
                    argumentsBuilder.Append($" +app_update {appId}");
                    if (forceValidate)
                    {
                        argumentsBuilder.Append(" validate");
                    }
                }

                if (!string.IsNullOrEmpty(profile.Arguments))
                {
                    argumentsBuilder.Append($" {profile.Arguments.Trim()}");
                }
                argumentsBuilder.Append(" +quit");

                string arguments = argumentsBuilder.ToString();
                string safeArguments = Regex.Replace(arguments, @"\+login\s+\S+\s+\S+", "+login [credentials]");
                _logger.LogInformation("Chạy SteamCMD cho '{ProfileName}' với tham số: {SafeArguments}", profile.Name, safeArguments);

                steamCmdProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = steamCmdPath,
                        Arguments = arguments,
                        WorkingDirectory = steamCmdDir,
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
                var recentOutputMessages = new ConcurrentDictionary<string, byte>();
                int maxRecentMessages = 50;
                System.Timers.Timer outputTimer = null;

                async Task SendBufferedOutput()
                {
                    string outputToSend;
                    lock (outputBuffer)
                    {
                        if (outputBuffer.Length == 0) return;
                        outputToSend = outputBuffer.ToString();
                        outputBuffer.Clear();
                    }
                    if (!string.IsNullOrWhiteSpace(outputToSend))
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", outputToSend.TrimEnd());
                    }
                }

                outputTimer = new System.Timers.Timer(250);
                outputTimer.Elapsed += async (sender, e) => await SendBufferedOutput();
                outputTimer.AutoReset = true;

                steamCmdProcess.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null) return;
                    string line = e.Data.Trim();
                    if (string.IsNullOrEmpty(line)) return;

                    Regex[] linesToRemoveRegex = {
                new Regex(@"^Redirecting stderr to '.*steamcmd\\logs\\stderr\.txt'$", RegexOptions.Compiled),
                new Regex(@"^Logging directory: '.*steamcmd/logs'$", RegexOptions.Compiled),
                new Regex(@"^\[\s*0%\] Checking for available updates\.\.\.$", RegexOptions.Compiled),
                new Regex(@"^\[----\].*Verifying installation\.\.\.$", RegexOptions.Compiled),
                new Regex(@"^Loading Steam API\.\.\.OK$", RegexOptions.Compiled),
                new Regex(@"^Logging in user '.*'", RegexOptions.Compiled),
                new Regex(@"^Logging in using username/password\.$", RegexOptions.Compiled),
                new Regex(@"^Waiting for client config\.\.\.OK$", RegexOptions.Compiled),
                new Regex(@"^Waiting for user info\.\.\.OK$", RegexOptions.Compiled),
                new Regex(@"^Unloading Steam API\.\.\.OK$", RegexOptions.Compiled),
                new Regex(@"^Steam Console Client \(c\) Valve Corporation - version \d+$", RegexOptions.Compiled),
                new Regex(@"^-- type 'quit' to exit --$", RegexOptions.Compiled),
            };

                    var loginSuccessRegex = new Regex(@"^Logging in user '.*' \[U:1:\d+\] to Steam Public\.\.\.OK$", RegexOptions.Compiled);

                    if (invalidPasswordRegex.IsMatch(line))
                    {
                        _logger.LogError($"Phát hiện lỗi đăng nhập (Sai mật khẩu) từ output SteamCMD cho profile '{profile.Name}'.");
                        _ = SafeSendLogAsync(profile.Name, "Error", "Lỗi đăng nhập: Sai tài khoản hoặc mật khẩu.");
                        _lastRunHadLoginError = true;
                        return;
                    }
                    if (notOnlineErrorRegex.IsMatch(line))
                    {
                        _logger.LogError($"Phát hiện lỗi không online/không đăng nhập từ output SteamCMD cho profile '{profile.Name}'.");
                        _ = SafeSendLogAsync(profile.Name, "Error", "Lỗi kết nối Steam hoặc không đăng nhập được (SteamCMD output).");
                        _lastRunHadLoginError = true;
                        return;
                    }

                    if (loginSuccessRegex.IsMatch(line))
                    {
                        _logger.LogInformation($"Đăng nhập Steam thành công cho profile '{profile.Name}' (SteamCMD output).");
                        _ = SafeSendLogAsync(profile.Name, "Success", "Đăng nhập Steam thành công.");
                    }

                    if (linesToRemoveRegex.Any(regex => regex.IsMatch(line)))
                    {
                        return;
                    }

                    if (recentOutputMessages.TryAdd(line, 0))
                    {
                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(line);
                        }

                        if (recentOutputMessages.Count > maxRecentMessages * 1.5)
                        {
                            var keysToRemove = recentOutputMessages.Keys.Take(recentOutputMessages.Count - maxRecentMessages).ToList();
                            foreach (var key in keysToRemove) recentOutputMessages.TryRemove(key, out _);
                        }

                        // Xử lý lỗi app (bắt tất cả lỗi "Error! App")
                        // Xử lý lỗi app (bắt tất cả lỗi "Error! App")
                        var matchErrorApp = errorAppRegex.Match(line);
                        if (matchErrorApp.Success)
                        {
                            // Capture only the App ID
                            string failedAppId = matchErrorApp.Groups[1].Value;
                            _logger.LogError($"Phát hiện lỗi cập nhật cho App ID {failedAppId} trong log của profile '{profile.Name}'.");

                            // Add to set to ensure uniqueness
                            if (appIdsToRetry.Add(failedAppId))
                            {
                                _ = Task.Run(async () =>
                                {
                                    var appInfo = await _steamApiService.GetAppUpdateInfo(failedAppId);
                                    string gameName = appInfo?.Name ?? failedAppId;
                                    await SafeSendLogAsync(profile.Name, "Error", $"Lỗi thời gian thực: Cập nhật thất bại '{gameName}' ({failedAppId}). Sẽ thử lại với validate.");
                                });
                            }
                        }
                    } // Kết thúc if (recentOutputMessages.TryAdd(line, 0))
                }; // Kết thúc steamCmdProcess.OutputDataReceived

                steamCmdProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    string errorLine = e.Data.Trim();
                    if (errorLine.Contains("Invalid platform") || errorLine.Contains("missing loadexisting"))
                    {
                        _logger.LogWarning("SteamCMD Stderr ({ProfileName}): {Data}", profile.Name, errorLine);
                        _ = _hubContext.Clients.All.SendAsync("ReceiveLog", $"CẢNH BÁO SteamCMD: {errorLine}");
                    }
                    else
                    {
                        _logger.LogError("SteamCMD Error ({ProfileName}): {Data}", profile.Name, errorLine);
                        _ = _hubContext.Clients.All.SendAsync("ReceiveLog", $"LỖI SteamCMD: {errorLine}");
                    }
                };

                _steamCmdProcesses[profileId] = steamCmdProcess;
                steamCmdProcess.Start();
                profile.Pid = steamCmdProcess.Id;
                await _profileService.UpdateProfile(profile);

                steamCmdProcess.BeginOutputReadLine();
                steamCmdProcess.BeginErrorReadLine();
                outputTimer.Start();

                bool exited = false;
                while (!exited)
                {
                    exited = steamCmdProcess.WaitForExit(500);
                    if (_lastRunHadLoginError)
                    {
                        _logger.LogWarning("Phát hiện lỗi đăng nhập trong khi SteamCMD đang chạy cho profile '{ProfileName}'. Sẽ dừng tiến trình.", profile.Name);
                        await KillProcessAsync(steamCmdProcess, profile.Name);
                        exited = true;
                    }
                }

                // Phần cuối của phương thức RunSteamCmdProcessAsync()
                // Bắt đầu từ phần kiểm tra runResult.Success

                runResult.ExitCode = _lastRunHadLoginError ?
                    -95 : steamCmdProcess.ExitCode;

                outputTimer.Stop();
                await SendBufferedOutput();

                _steamCmdProcesses.TryRemove(profileId, out _);

                // Success if no login error and SteamCMD process exited with success code (0 or 2)
                runResult.Success = !_lastRunHadLoginError && (runResult.ExitCode == 0 || runResult.ExitCode == 2);

                // ĐÂY LÀ PHẦN CẦN SỬA
                if (runResult.Success)
                {
                    if (appIdsToUpdate.Count == 1)
                    {
                        // Nếu chỉ chạy 1 app
                        var appId = appIdsToUpdate.First();
                        var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                        string gameName = appInfo?.Name ?? appId;

                        _logger.LogInformation("Cập nhật '{GameName}' (AppID: {AppId}) cho '{ProfileName}' hoàn tất thành công. Exit Code: {ExitCode}",
                            gameName, appId, profile.Name, runResult.ExitCode);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cập nhật '{gameName}' (AppID: {appId}) hoàn tất thành công (Code: {runResult.ExitCode}).");
                    }
                    else
                    {
                        _logger.LogInformation("Cập nhật Game cho '{ProfileName}' hoàn tất thành công. Exit Code: {ExitCode}", profile.Name, runResult.ExitCode);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cập nhật Game cho '{profile.Name}' hoàn tất thành công (Code: {runResult.ExitCode}).");
                    }
                }
                else
                {
                    string errorReason;
                    if (_lastRunHadLoginError)
                    {
                        errorReason = $"Lỗi đăng nhập hoặc thiếu thông tin đăng nhập (Internal Code: {runResult.ExitCode})";
                    }
                    else
                    {
                        errorReason = $"Exit Code: {runResult.ExitCode}";
                    }
                    _logger.LogError("Cập nhật Game cho cho '{ProfileName}' thất bại. Lý do: {Reason}", profile.Name, errorReason);
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cập nhật Game cho '{profile.Name}' thất bại. Lý do: {errorReason}.");
                }
                // KẾT THÚC PHẦN CẦN SỬA
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng khi chạy SteamCMD cho profile {ProfileName}: {Message}", profile?.Name ?? "Unknown", ex.Message);
                await SafeSendLogAsync(profile?.Name ?? $"Profile {profileId}", "Error", $"Lỗi nghiêm trọng khi chạy SteamCMD: {ex.Message}");
                runResult.Success = false;
                runResult.ExitCode = -1;
                _lastRunHadLoginError = false;
            }
            finally
            {
                steamCmdProcess?.Dispose();
            }

            // Added based on 1.txt point 35
            // Nếu có các app cần chạy lại với validate
            if (appIdsToRetry.Count > 0)
            {
                _logger.LogInformation("Phát hiện {Count} app bị lỗi cần chạy lại với validate: {AppIds}",
                    appIdsToRetry.Count, string.Join(", ", appIdsToRetry));

                await SafeSendLogAsync(profile.Name, "Info", $"Chuẩn bị chạy lại {appIdsToRetry.Count} app bị lỗi với validate...");

                // Dừng process hiện tại nếu chưa thoát
                await Task.Delay(1000);

                // Giờ đây loginCommand đã có sẵn để sử dụng
                foreach (var appId in appIdsToRetry)
                {
                    // Phần còn lại giữ nguyên
                }
            }

            return runResult;
        }


        public async Task<Dictionary<string, string>> ReadAppManifest(string steamappsDir, string appId)
        {
            string manifestFilePath = Path.Combine(steamappsDir, $"appmanifest_{appId}.acf");
            if (!File.Exists(manifestFilePath))
            {
                _logger.LogInformation($"File manifest không tồn tại cho App ID {appId}: {manifestFilePath}");
                return null;
            }

            try
            {
                string content = await File.ReadAllTextAsync(manifestFilePath);
                var manifestData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                var regex = new Regex(@"""(?<key>[^""]+)""\s+""(?<value>[^""]*)""", RegexOptions.Compiled);
                var matches = regex.Matches(content);

                foreach (Match match in matches)
                {
                    if (match.Success)
                    {
                        manifestData[match.Groups["key"].Value] = match.Groups["value"].Value;
                    }
                }

                if (!manifestData.ContainsKey("SizeOnDisk"))
                {
                    foreach (var key in new[] { "size", "installsize", "download_size" })
                    {
                        if (manifestData.ContainsKey(key) && long.TryParse(manifestData[key], out long size))
                        {
                            manifestData["SizeOnDisk"] = size.ToString();
                            break;
                        }
                    }
                }

                return manifestData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi đọc file manifest {manifestFilePath}");
                return null;
            }
        }


        // Trong phương thức RunSpecificAppAsync(), thêm thông báo rõ ràng hơn
        public async Task<bool> RunSpecificAppAsync(int profileId, string appId)
        {
            var profile = await _profileService.GetProfileById(profileId);
            if (profile == null)
            {
                _logger.LogError("RunSpecificAppAsync: Không tìm thấy profile ID {ProfileId} để chạy App ID {AppId}", profileId, appId);
                await SafeSendLogAsync($"Profile {profileId}", "Error", $"Không tìm thấy profile ID {profileId} để chạy App ID {appId}");
                return false;
            }

            // Lấy tên game trước khi chạy để thông báo
            var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
            string gameName = appInfo?.Name ?? $"AppID {appId}";

            _logger.LogInformation("RunSpecificAppAsync: Chạy cập nhật riêng App ID {AppId} ({GameName}) cho profile {ProfileName} (ID: {ProfileId})",
                appId, gameName, profile.Name, profileId);

            await SafeSendLogAsync(profile.Name, "Info", $"Bắt đầu cập nhật riêng '{gameName}' (AppID: {appId})...");

            // Tối ưu: Tránh dừng các tiến trình không liên quan
            if (_steamCmdProcesses.TryGetValue(profileId, out var existingProcess))
            {
                await KillProcessAsync(existingProcess, profile.Name);
                await Task.Delay(1000); // Giảm thời gian chờ
            }

            // Cập nhật trạng thái profile thành "Running"
            profile.Status = "Running";
            profile.StartTime = DateTime.Now;
            await _profileService.UpdateProfile(profile);

            // Chạy cập nhật chỉ cho app ID cụ thể này
            List<string> appList = new List<string>() { appId };

            // Tối ưu: Thực hiện cập nhật 1 lần không có validate trước
            var result = await RunSteamCmdProcessAsync(profile, profileId, appList, forceValidate: false);

            // Tối ưu: Chỉ kiểm tra kết quả, không thực hiện thêm bước cập nhật nào nếu không cần thiết
            bool success = result.Success;

            // Cập nhật trạng thái profile
            profile.Status = "Stopped";
            profile.StopTime = DateTime.Now;
            profile.LastRun = DateTime.Now;
            await _profileService.UpdateProfile(profile);

            // Reset cờ cập nhật cho app đã cập nhật
            await _dependencyManagerService.ResetUpdateFlagsAsync(profile.Id, appId);

            if (success)
            {
                await SafeSendLogAsync(profile.Name, "Success", $"Đã cập nhật thành công '{gameName}' (AppID: {appId})");
            }
            else
            {
                await SafeSendLogAsync(profile.Name, "Error", $"Cập nhật '{gameName}' (AppID: {appId}) thất bại");
            }

            return success;
        }

        public async Task RunAllProfilesAsync()
        {
            if (_isRunningAllProfiles)
            {
                _logger.LogWarning("RunAllProfilesAsync called but already running.");
                await SafeSendLogAsync("System", "Warning", "Đang có quá trình chạy tất cả profile, không thể thực hiện yêu cầu này.");
                return;
            }

            _isRunningAllProfiles = true;

            try
            {
                var profiles = await _profileService.GetAllProfiles();
                if (!profiles.Any())
                {
                    await SafeSendLogAsync("System", "Warning", "Không có cấu hình nào để chạy.");
                    return;
                }

                _currentProfileIndex = 0;

                _logger.LogInformation("RunAllProfilesAsync: Bắt đầu thêm tất cả profile vào hàng đợi cập nhật...");
                await SafeSendLogAsync("System", "Info", "Bắt đầu thêm tất cả profile vào hàng đợi cập nhật...");

                _logger.LogInformation("RunAllProfilesAsync: Calling StopAllProfilesAsync as preparation...");
                await StopAllProfilesAsync();
                _logger.LogInformation("RunAllProfilesAsync: StopAllProfilesAsync preparation completed. Waiting {Delay}ms...", RetryDelayMs);
                await Task.Delay(RetryDelayMs);

                _logger.LogInformation("RunAllProfilesAsync: Starting profile queueing loop...");
                foreach (var profile in profiles)
                {
                    _currentProfileIndex++;
                    await SafeSendLogAsync("System", "Info", $"Đang thêm profile ({_currentProfileIndex}/{profiles.Count}): {profile.Name} vào hàng đợi...");
                    await QueueProfileForUpdate(profile.Id);
                    await Task.Delay(500);
                }
                _logger.LogInformation("RunAllProfilesAsync: Finished profile queueing loop.");

                await SafeSendLogAsync("System", "Info", "Đã thêm tất cả profile vào hàng đợi. Bộ xử lý hàng đợi sẽ chạy chúng tuần tự.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong RunAllProfilesAsync.");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chuẩn bị chạy tất cả profile: {ex.Message}");
            }
            finally
            {
                // Không reset _isRunningAllProfiles ở đây
                // _isRunningAllProfiles sẽ được giữ true cho đến khi tất cả profile đã được xử lý xong
            }
        }


        public async Task StopAllProfilesAsync()
        {
            _logger.LogInformation("StopAllProfilesAsync: Đang dừng tất cả các profile và xóa hàng đợi...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profile và xóa hàng đợi.");

            // Xóa hàng đợi
            int clearedCount = 0;
            while (_updateQueue.TryDequeue(out _)) { clearedCount++; }
            if (clearedCount > 0)
                _logger.LogInformation("StopAllProfilesAsync: Đã xóa {Count} mục khỏi hàng đợi cập nhật.", clearedCount);

            // Reset các cờ, nhưng không reset _isRunningAllProfiles để không ảnh hưởng đến việc đang chạy
            _cancelAutoRun = false;
            _lastRunHadLoginError = false;

            // Dừng tất cả tiến trình
            await KillAllSteamCmdProcessesAsync();
            await Task.Delay(1000);

            // Cập nhật trạng thái các profile đang chạy
            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.Status == "Running"))
            {
                _logger.LogInformation("StopAllProfilesAsync: Cập nhật trạng thái profile '{ProfileName}' thành Stopped do StopAll.", profile.Name);
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);
            }

            await SafeSendLogAsync("System", "Success", "Đã dừng tất cả các profile và xóa hàng đợi.");
        }

        public async Task<bool> StopProfileAsync(int profileId)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile ID {ProfileId} để dừng", profileId);
                    return false;
                }

                if (_steamCmdProcesses.TryRemove(profileId, out var process))
                {
                    await KillProcessAsync(process, profile.Name);
                    process.Dispose();
                }

                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);

                _logger.LogInformation("Đã dừng profile ID {ProfileId} ('{ProfileName}')", profileId, profile.Name);
                await SafeSendLogAsync(profile.Name, "Success", $"Đã dừng profile {profile.Name}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng profile ID {ProfileId}", profileId);
                await SafeSendLogAsync($"Profile {profileId}", "Error", $"Lỗi khi dừng: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> RestartProfileAsync(int profileId)
        {
            var profile = await _profileService.GetProfileById(profileId);
            if (profile == null) return false;

            await SafeSendLogAsync(profile.Name, "Info", $"Đang khởi động lại profile {profile.Name}...");

            if (_steamCmdProcesses.TryRemove(profileId, out var process))
            {
                await KillProcessAsync(process, profile.Name);
                process.Dispose();
            }

            await Task.Delay(RetryDelayMs);

            return await QueueProfileForUpdate(profileId);
        }

        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang tắt dịch vụ SteamCMD...");
            await SafeSendLogAsync("System", "Info", "Đang tắt ứng dụng...");

            _scheduleTimer?.Stop();
            _scheduleTimer?.Dispose();
            _hubMessageCleanupTimer?.Stop();
            _hubMessageCleanupTimer?.Dispose();

            await StopAllProfilesAsync();

            _logger.LogInformation("Đã hoàn thành tắt dịch vụ SteamCMD.");
            await SafeSendLogAsync("System", "Success", "Đã hoàn thành dừng process.");
        }
    }
}
#endregion