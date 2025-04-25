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
using Newtonsoft.Json; // Added for JSON deserialization
using System.Text.RegularExpressions; // Added for regex to extract App IDs and parse manifest files

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
        private readonly SteamApiService _steamApiService; // Inject SteamApiService

        private const int MaxLogEntries = 5000;
        // Increased delays for robustness
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

        // Cache for recent hub messages to prevent console duplication
        private readonly ConcurrentDictionary<string, DateTime> _recentHubMessages = new ConcurrentDictionary<string, DateTime>();
        private readonly System.Timers.Timer _hubMessageCleanupTimer;
        private const int HubMessageCacheDurationSeconds = 5;
        private const int HubMessageCleanupIntervalMs = 10000;


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

        // Queue management
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
            SteamApiService steamApiService) // Inject SteamApiService
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _settingsService = settingsService;
            _encryptionService = encryptionService;
            _logFileReader = logFileReader;
            _steamApiService = steamApiService; // Assign injected service

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

                // Avoid excessive duplicate internal log entries
                if (_recentLogMessages.Contains(logKey))
                {
                    return;
                }
                _recentLogMessages.Add(logKey);
                if (_recentLogMessages.Count > _maxRecentLogMessages)
                {
                    _recentLogMessages.Clear(); // Simple clear when limit reached
                }

                // Check short-term cache before sending to hub to avoid console spam
                if (!_recentHubMessages.TryGetValue(message, out var lastSentTime) || (DateTime.Now - lastSentTime).TotalSeconds > HubMessageCacheDurationSeconds)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", message);
                    _recentHubMessages[message] = DateTime.Now;
                }

                // Always add to internal log history
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

        /// <summary>
        /// Cleans up old entries from the _recentHubMessages cache.
        /// </summary>
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

            // Stop existing processes before queueing
            await KillAllSteamCmdProcessesAsync();

            // Add auto-run profiles to the update queue
            foreach (var profile in autoRunProfiles)
            {
                await QueueProfileForUpdate(profile.Id);
                await Task.Delay(500); // Short delay between adding to queue
            }

            await SafeSendLogAsync("System", "Success", "Đã thêm tất cả profile Auto Run vào hàng đợi cập nhật");
        }

        private async Task CheckScheduleAsync()
        {
            try
            {
                var settings = await _settingsService.LoadSettingsAsync();
                if (!settings.AutoRunEnabled || _isRunningAllProfiles) // Also check if manual run-all is active
                {
                    return;
                }

                var now = DateTime.Now;
                TimeSpan timeSinceLastRun = now - _lastAutoRunTime;
                int intervalHours = settings.AutoRunIntervalHours;

                if (_lastAutoRunTime == DateTime.MinValue || timeSinceLastRun.TotalHours >= intervalHours)
                {
                    _logger.LogInformation("Đang thêm tất cả profile vào hàng đợi theo khoảng thời gian {0} giờ", intervalHours);
                    await StartAllAutoRunProfilesAsync(); // This now queues profiles
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
                await Task.Delay(500); // Give time for termination
                _logger.LogInformation("Đã dừng tiến trình SteamCMD cho {ProfileName}", profileName);
                return process.HasExited; // Check if actually exited
            }
            catch (Exception ex)
            {
                // Log as warning, don't necessarily stop execution flow if kill fails
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
                // Stop tracked processes first
                foreach (var kvp in _steamCmdProcesses.ToArray()) // Use ToArray for safe iteration while removing
                {
                    if (!await KillProcessAsync(kvp.Value, $"Profile {kvp.Key}"))
                    {
                        success = false; // Mark failure if any tracked process couldn't be stopped
                    }
                    _steamCmdProcesses.TryRemove(kvp.Key, out _); // Remove from tracking
                }

                // Use taskkill as a safety net for any remaining steamcmd.exe processes
                _logger.LogInformation("Sử dụng taskkill để đảm bảo tất cả steamcmd.exe đã dừng...");
                CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe /T", ProcessExitTimeoutMs); // Added /T to kill child processes
                await Task.Delay(1500); // Wait after taskkill

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
                // Ensure no SteamCMD processes are running
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs); // Wait after killing

                // Attempt to delete existing executable if it's locked
                if (File.Exists(steamCmdPath))
                {
                    try
                    {
                        File.Delete(steamCmdPath);
                        await Task.Delay(1000); // Wait after delete
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
                            // Decide whether to throw or continue based on severity
                            // throw; // Re-throwing might be appropriate here
                        }
                    }
                    catch (Exception ex) // Catch other potential delete errors
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

                // Download steamcmd.zip
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5); // Reasonable timeout for download
                    await SafeSendLogAsync("System", "Info", $"Đang tải SteamCMD từ {downloadUrl}...");
                    _logger.LogInformation("Bắt đầu tải SteamCMD từ {Url}", downloadUrl);

                    if (File.Exists(zipPath))
                    {
                        try { File.Delete(zipPath); } catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip cũ."); }
                    }

                    var response = await httpClient.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode(); // Throws if download fails
                    using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                    await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                }

                // Ensure no processes interfere with extraction
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(3000); // Wait after killing

                // Extract
                try
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, steamCmdDir, true); // Overwrite existing files
                    await SafeSendLogAsync("System", "Success", "Đã giải nén SteamCMD thành công.");
                }
                catch (IOException ioEx) // Handle potential file lock errors during extraction
                {
                    _logger.LogError(ioEx, "Lỗi IO khi giải nén SteamCMD. Thử dùng PowerShell.");
                    await SafeSendLogAsync("System", "Error", $"Lỗi IO khi giải nén: {ioEx.Message}. Thử dùng PowerShell...");
                    CmdHelper.RunCommand($"powershell -command \"Expand-Archive -Path '{zipPath}' -DestinationPath '{steamCmdDir}' -Force\"", 60000); // PS fallback with timeout
                    await Task.Delay(2000); // Wait after PS command
                }
                catch (Exception ex) // Catch other extraction errors
                {
                    _logger.LogError(ex, "Lỗi không mong muốn khi giải nén SteamCMD.");
                    await SafeSendLogAsync("System", "Error", $"Lỗi giải nén không mong muốn: {ex.Message}");
                    throw; // Re-throw other critical extraction errors
                }
                finally // Ensure zip file is deleted
                {
                    try { File.Delete(zipPath); } catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip sau khi giải nén."); }
                }


                // Final check for steamcmd.exe
                if (!File.Exists(steamCmdPath))
                {
                    throw new Exception("Cài đặt thất bại. Không tìm thấy steamcmd.exe sau khi giải nén.");
                }

                // Basic validation of the executable file
                try
                {
                    FileInfo fileInfo = new FileInfo(steamCmdPath);
                    if (fileInfo.Length < 10000) // Basic sanity check for file size
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
                    throw; // Re-throw validation error
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
                throw; // Re-throw other critical errors
            }
        }


        private string GetSteamCmdLogPath(int profileId) // Kept for potential future direct log file access
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string logsDir = Path.Combine(steamCmdDir, "logs");
            Directory.CreateDirectory(logsDir); // Ensure exists
            return Path.Combine(logsDir, $"console_log_{profileId}.txt");
        }
        #endregion

        #region Folder Setup
        /// <summary>
        /// Prepares the folder structure: deletes old local steamapps and creates a symbolic link.
        /// Includes robust deletion attempts with process killing.
        /// </summary>
        /// <returns>True if successful, false otherwise.</returns>
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

            // 1. Ensure the target directory exists
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

            // 2. Delete existing local steamapps link/directory (with retries and killing processes)
            if (Directory.Exists(localSteamappsLinkDir) || File.Exists(localSteamappsLinkDir)) // Check for file junction too
            {
                _logger.LogInformation($"Đang cố gắng xóa thư mục/liên kết steamapps cục bộ cũ: {localSteamappsLinkDir}");
                bool deleted = false;
                int maxRetries = 10; // Reduced retries slightly, increased delays instead
                int currentRetryDelay = 3000; // Start with 3 seconds

                for (int i = 0; i < maxRetries; i++)
                {
                    try
                    {
                        _logger.LogInformation($"Thử xóa thư mục/liên kết {localSteamappsLinkDir} lần {i + 1}/{maxRetries}...");
                        // Use rmdir for directories, del for file junctions
                        if ((File.GetAttributes(localSteamappsLinkDir) & FileAttributes.Directory) == FileAttributes.Directory)
                        {
                            CmdHelper.RunCommand($"rmdir /S /Q \"{localSteamappsLinkDir}\"", 45000); // Force remove directory
                        }
                        else
                        {
                            CmdHelper.RunCommand($"del /F /Q \"{localSteamappsLinkDir}\"", 15000); // Force remove file/junction
                        }

                        await Task.Delay(1000); // Wait a bit after command

                        if (!Directory.Exists(localSteamappsLinkDir) && !File.Exists(localSteamappsLinkDir))
                        {
                            _logger.LogInformation("Đã xóa thành công thư mục/liên kết steamapps cục bộ cũ.");
                            deleted = true;
                            break;
                        }
                        else
                        {
                            _logger.LogWarning($"Thư mục/liên kết steamapps cục bộ cũ vẫn tồn tại sau lần thử {i + 1}. Dừng SteamCMD và đợi.");
                            await SafeSendLogAsync("System", "Warning", $"Thư mục/liên kết steamapps cũ vẫn tồn tại (thử {i + 1}). Đang dừng SteamCMD...");
                            await KillAllSteamCmdProcessesAsync();
                            await Task.Delay(currentRetryDelay); // Wait longer after killing
                            currentRetryDelay += 2000; // Increase delay for next retry
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Lỗi khi xóa thư mục/liên kết steamapps cục bộ cũ lần thử {i + 1}");
                        await SafeSendLogAsync("System", "Error", $"Lỗi khi xóa (thử {i + 1}): {ex.Message}");
                        await KillAllSteamCmdProcessesAsync(); // Kill processes on error too
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

            // 3. Create the symbolic link
            if (!Directory.Exists(localSteamappsLinkDir)) // Double-check it's gone before creating link
            {
                _logger.LogInformation($"Đang tạo liên kết tượng trưng từ \"{steamappsTargetDir}\" đến \"{localSteamappsLinkDir}\"");
                try
                {
                    // Ensure the parent directory for the link exists
                    Directory.CreateDirectory(Path.GetDirectoryName(localSteamappsLinkDir));

                    CmdHelper.RunCommand($"mklink /D \"{localSteamappsLinkDir}\" \"{steamappsTargetDir}\"", 15000);
                    await Task.Delay(2000); // Wait after link creation

                    // Verify link creation by checking existence
                    if (Directory.Exists(localSteamappsLinkDir))
                    {
                        _logger.LogInformation("Đã tạo liên kết tượng trưng thành công.");
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
                // Should not happen if deletion logic worked, but log defensively
                _logger.LogError("Thư mục/liên kết steamapps cục bộ vẫn tồn tại mặc dù đã cố gắng xóa. Bỏ qua tạo liên kết.");
                await SafeSendLogAsync("System", "Error", "Thư mục/liên kết steamapps cục bộ vẫn tồn tại. Bỏ qua tạo liên kết.");
                return false;
            }
        }
        #endregion

        #region Public API Methods (Queueing and Execution)

        /// <summary>
        /// Adds a profile to the update queue.
        /// </summary>
        public async Task<bool> QueueProfileForUpdate(int profileId)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile ID {ProfileId} để thêm vào hàng đợi", profileId);
                    return false;
                }

                if (_updateQueue.Contains(profileId))
                {
                    _logger.LogInformation("Profile ID {ProfileId} ('{ProfileName}') đã có trong hàng đợi cập nhật", profileId, profile.Name);
                    await SafeSendLogAsync(profile.Name, "Info", $"Profile {profile.Name} đã có trong hàng đợi.");
                    return true; // Already queued
                }

                _updateQueue.Enqueue(profileId);
                _logger.LogInformation("Đã thêm profile ID {ProfileId} ('{ProfileName}') vào hàng đợi cập nhật", profileId, profile.Name);
                await SafeSendLogAsync(profile.Name, "Info", $"Đã thêm Profile {profile.Name} vào hàng đợi cập nhật.");

                // Start the queue processor if it's not running
                StartQueueProcessorIfNotRunning();

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm profile ID {ProfileId} vào hàng đợi cập nhật", profileId);
                await SafeSendLogAsync($"Profile {profileId}", "Error", $"Lỗi khi thêm vào hàng đợi: {ex.Message}");
                return false;
            }
        }

        private void StartQueueProcessorIfNotRunning()
        {
            // Use lock for thread-safe check and start
            lock (_queueProcessorTask ?? new object()) // Lock on the task itself or a temporary object if null
            {
                if (_queueProcessorTask == null || _queueProcessorTask.IsCompleted)
                {
                    _logger.LogInformation("Khởi động bộ xử lý hàng đợi...");
                    _queueProcessorTask = Task.Run(ProcessUpdateQueueAsync);
                }
            }
        }


        /// <summary>
        /// Processes the update queue sequentially.
        /// </summary>
        private async Task ProcessUpdateQueueAsync()
        {
            // Prevent concurrent execution of the processor itself
            if (_isProcessingQueue)
            {
                _logger.LogWarning("ProcessUpdateQueueAsync called but already running. Exiting."); // Added log
                return;
            }
            _isProcessingQueue = true;
            _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Set _isProcessingQueue = true"); // Added log

            try
            {
                _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Bắt đầu xử lý hàng đợi (Queue Count: {Count})", _updateQueue.Count); // Added log + count
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Bắt đầu xử lý hàng đợi cập nhật (Số lượng: {_updateQueue.Count})"); // Added count

                while (_updateQueue.TryDequeue(out int profileId))
                {
                    var profile = await _profileService.GetProfileById(profileId);
                    string profileName = profile?.Name ?? $"Profile {profileId}";
                    _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Dequeued {ProfileName} (ID: {ProfileId})", profileName, profileId); // Added log

                    try
                    {
                        _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Processing {ProfileName}...", profileName); // Added log
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đang xử lý cập nhật cho {profileName} (ID: {profileId})...");

                        // >>>>> DÒNG GÂY LỖI ĐÃ BỊ XÓA <<<<<
                        // Dòng "await StopAllProfilesAsync();" đã được loại bỏ khỏi đây.
                        // Việc dừng các tiến trình SteamCMD đã được xử lý trong ExecuteProfileUpdateAsync
                        // hoặc cần được gọi trước khi bắt đầu xử lý toàn bộ hàng đợi (ví dụ: trong RunAllProfilesAsync).
                        // await Task.Delay(RetryDelayMs); // Bạn có thể giữ dòng chờ này nếu cần thiết giữa các lần chạy profile

                        _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Calling ExecuteProfileUpdateAsync for {ProfileName}...", profileName); // Added log

                        // Execute the actual update logic
                        bool success = await ExecuteProfileUpdateAsync(profileId);
                        _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: ExecuteProfileUpdateAsync completed for {ProfileName}. Success: {SuccessState}", profileName, success); // Added log


                        if (success)
                        {
                            _logger.LogInformation("Đã xử lý cập nhật thành công cho {ProfileName} (ID: {ProfileId})", profileName, profileId);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cập nhật thành công cho {profile.Name} (ID: {profileId})");
                        }
                        else
                        {
                            _logger.LogWarning("Xử lý cập nhật không thành công cho {ProfileName} (ID: {ProfileId})", profileName, profileId);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cập nhật KHÔNG thành công cho {profile.Name} (ID: {profileId}). Kiểm tra log.");
                        }

                        _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Waiting 3000ms before next item..."); // Added log
                        // Wait briefly before processing the next item
                        await Task.Delay(3000); // Giữ dòng chờ này giúp tránh quá tải hệ thống khi xử lý nhiều profile liên tiếp
                        _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Finished wait."); // Added log
                    }
                    catch (Exception ex)
                    {
                        // Existing error logging
                        _logger.LogError(ex, "Lỗi khi xử lý cập nhật cho {ProfileName} (ID: {ProfileId}) từ hàng đợi", profileName, profileId);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi xử lý cập nhật cho {profileName} (ID: {profileId}): {ex.Message}");
                        if (profile != null)
                        {
                            profile.Status = "Error";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                        }
                    }
                    _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: End of loop iteration for {ProfileName}. Remaining queue count: {Count}", profileName, _updateQueue.Count); // Added log
                } // End while loop (queue processing)

                _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Queue is now empty."); // Added log
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã xử lý xong hàng đợi cập nhật");
            }
            catch (Exception ex)
            {
                // Existing error logging
                _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình xử lý hàng đợi cập nhật");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi nghiêm trọng trong bộ xử lý hàng đợi: {ex.Message}");
            }
            finally
            {
                _logger.LogInformation(">>>>>>>>>> ProcessUpdateQueueAsync: Setting _isProcessingQueue = false"); // Added log
                _isProcessingQueue = false; // Ensure this is reset
            }
        }

        /// <summary>
        /// Queues a single profile for update.
        /// </summary>
        public async Task<bool> RunProfileAsync(int id)
        {
            return await QueueProfileForUpdate(id); // Simply queue it
        }

        /// <summary>
        /// Executes the core update logic for a profile, including retries.
        /// This is called by the queue processor.
        /// </summary>
        private async Task<bool> ExecuteProfileUpdateAsync(int id)
        {
            var profile = await _profileService.GetProfileById(id);
            if (profile == null)
            {
                _logger.LogError("Profile ID {ProfileId} không tìm thấy để thực thi cập nhật", id);
                await SafeSendLogAsync($"Profile {id}", "Error", $"Profile ID {id} không tìm thấy để thực thi cập nhật");
                return false;
            }

            await SafeSendLogAsync(profile.Name, "Info", $"Chuẩn bị cập nhật '{profile.Name}' (AppID: {profile.AppID})...");

            // Stop any potentially running processes (redundant if called by queue processor, but safe)
            await KillAllSteamCmdProcessesAsync(); // Ensure SteamCMD is stopped before starting a new process
            await Task.Delay(RetryDelayMs);


            // 1. Check/Install SteamCMD
            if (!await IsSteamCmdInstalled())
            {
                await SafeSendLogAsync(profile.Name, "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                try
                {
                    await InstallSteamCmd();
                    if (!await IsSteamCmdInstalled()) // Verify after install attempt
                    {
                        throw new Exception("SteamCMD installation reported success but executable not found.");
                    }
                }
                catch (Exception installEx)
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"Không thể cài đặt SteamCMD: {installEx.Message}");
                    profile.Status = "Error"; // Set status to Error on installation failure
                    profile.StopTime = DateTime.Now;
                    await _profileService.UpdateProfile(profile);
                    return false;
                }
            }

            // 2. Check/Create Install Directory
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

            // 3. Prepare Folder Structure (Delete old link, create new one)
            string linkedSteamappsDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");
            if (!await PrepareFolderStructure(profile.InstallDirectory))
            {
                await SafeSendLogAsync(profile.Name, "Error", "Lỗi khi chuẩn bị cấu trúc thư mục (liên kết steamapps).");
                profile.Status = "Error";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);
                return false;
            }

            // 4. Identify App IDs to Update (Main + from manifests)
            var appIdsToUpdateInitial = new List<string> { profile.AppID };
            try
            {
                if (Directory.Exists(linkedSteamappsDir)) // Check if link exists after PrepareFolderStructure
                {
                    var manifestFiles = Directory.GetFiles(linkedSteamappsDir, "appmanifest_*.acf");
                    var regex = new Regex(@"appmanifest_(\d+)\.acf");
                    foreach (var file in manifestFiles)
                    {
                        var match = regex.Match(Path.GetFileName(file));
                        if (match.Success)
                        {
                            string foundAppId = match.Groups[1].Value;
                            if (foundAppId != profile.AppID && !appIdsToUpdateInitial.Contains(foundAppId))
                            {
                                appIdsToUpdateInitial.Add(foundAppId);
                                _logger.LogInformation($"Tìm thấy App ID phụ từ manifest: {foundAppId} cho profile {profile.Name}");
                            }
                        }
                    }
                }
                if (appIdsToUpdateInitial.Count > 1)
                {
                    await SafeSendLogAsync(profile.Name, "Info", $"Sẽ cập nhật {appIdsToUpdateInitial.Count} App ID (chính: {profile.AppID}, phụ: {string.Join(", ", appIdsToUpdateInitial.Where(a => a != profile.AppID))}).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc file appmanifest cho profile {ProfileName}", profile.Name);
                await SafeSendLogAsync(profile.Name, "Warning", $"Lỗi khi đọc file appmanifest: {ex.Message}. Chỉ cập nhật App ID chính.");
                // Continue with only the main AppID
                appIdsToUpdateInitial = new List<string> { profile.AppID };
            }


            // 5. Update Profile Status to Running
            profile.Status = "Running";
            profile.StartTime = DateTime.Now;
            profile.StopTime = DateTime.Now;
            profile.Pid = 0; // Reset PID before run
            await _profileService.UpdateProfile(profile);


            // --- Get Game Names for Logging ---
            var appNamesForLog = new Dictionary<string, string>();
            foreach (var appId in appIdsToUpdateInitial)
            {
                var appInfo = await _steamApiService.GetAppUpdateInfo(appId); // Use SteamApiService to get info/name
                appNamesForLog[appId] = appInfo?.Name ?? appId; // Use name from API if available, else AppID
            }


            // --- First Run: Update all identified App IDs (NO validate) ---
            await SafeSendLogAsync(profile.Name, "Info", $"Bắt đầu lần chạy cập nhật đầu tiên cho: {string.Join(", ", appNamesForLog.Select(kv => $"'{kv.Value}' ({kv.Key})"))}");
            var initialRunResult = await RunSteamCmdProcessAsync(profile, id, appIdsToUpdateInitial, forceValidate: false);


            // --- Short delay and ensure processes are stopped before validation check ---
            await KillAllSteamCmdProcessesAsync(); // Kill just in case the process didn't exit cleanly or hung
            await Task.Delay(RetryDelayMs);


            // --- Check Manifests and Identify Failures ---
            var failedAppIdsForRetry = new List<string>();
            await SafeSendLogAsync(profile.Name, "Info", "Đang kiểm tra kết quả cập nhật lần đầu từ file manifest...");

            foreach (var appId in appIdsToUpdateInitial)
            {
                var manifestData = await ReadAppManifest(linkedSteamappsDir, appId);
                string gameName = appNamesForLog.TryGetValue(appId, out var name) ? name : appId; // Get name from pre-fetched map

                if (manifestData == null)
                {
                    _logger.LogWarning($"Manifest cho '{gameName}' (AppID: {appId}) không tìm thấy sau lần chạy đầu. Đánh dấu để thử lại.");
                    await SafeSendLogAsync(profile.Name, "Warning", $"Manifest cho '{gameName}' ({appId}) không tìm thấy. Thử lại.");
                    failedAppIdsForRetry.Add(appId);
                }
                else if (!manifestData.TryGetValue("UpdateResult", out string updateResultValue) ||
                         (updateResultValue != "0" && updateResultValue != "2" && updateResultValue != "23")) // Check UpdateResult (0, 2, 23 are considered OK here)
                {
                    string resultText = manifestData.TryGetValue("UpdateResult", out var ur) ? $"UpdateResult: {ur}" : "UpdateResult không tồn tại";
                    _logger.LogWarning($"Cập nhật lần đầu cho '{gameName}' (AppID: {appId}) không thành công ({resultText}). Đánh dấu để thử lại.");
                    await SafeSendLogAsync(profile.Name, "Warning", $"Cập nhật '{gameName}' ({appId}) lần đầu không thành công ({resultText}). Thử lại.");
                    failedAppIdsForRetry.Add(appId);
                }
                else
                {
                    _logger.LogInformation($"Cập nhật lần đầu cho '{gameName}' (AppID: {appId}) thành công (UpdateResult: {updateResultValue}).");
                    // Optionally log success via SafeSendLogAsync if desired, but can be noisy
                }
            }


            // --- Second Run (if needed): Retry ONLY failed App IDs WITH validate ---
            bool retryRunSuccessful = true; // Assume success if no retry needed
            if (failedAppIdsForRetry.Any())
            {
                // Ensure clean state before retry
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs);

                var failedAppNamesForRetryLog = failedAppIdsForRetry
                    .Select(appId => $"'{appNamesForLog.GetValueOrDefault(appId, appId)}' ({appId})")
                    .ToList();

                _logger.LogWarning($"Phát hiện {failedAppIdsForRetry.Count} game cần thử lại với validate: {string.Join(", ", failedAppNamesForRetryLog)}. Đang thực hiện lần chạy thứ hai...");
                await SafeSendLogAsync(profile.Name, "Warning", $"Phát hiện {failedAppIdsForRetry.Count} game cần thử lại với validate. Bắt đầu lần chạy thứ hai...");

                // Run retry specifically for failed App IDs with forced validation
                var retryRunResult = await RunSteamCmdProcessAsync(profile, id, failedAppIdsForRetry, forceValidate: true);

                // --- Final Check After Retry ---
                await KillAllSteamCmdProcessesAsync(); // Kill after retry run
                await Task.Delay(RetryDelayMs);

                var failedAfterRetry = new List<string>();
                await SafeSendLogAsync(profile.Name, "Info", "Đang kiểm tra kết quả cuối cùng sau khi thử lại...");

                foreach (var appId in failedAppIdsForRetry) // Only check the ones that were retried
                {
                    var manifestData = await ReadAppManifest(linkedSteamappsDir, appId);
                    string gameName = appNamesForLog.GetValueOrDefault(appId, appId);

                    if (manifestData == null)
                    {
                        _logger.LogError($"Thử lại cho '{gameName}' (AppID: {appId}) thất bại. Manifest vẫn không tìm thấy.");
                        await SafeSendLogAsync(profile.Name, "Error", $"Thử lại cho '{gameName}' ({appId}) thất bại (không tìm thấy manifest).");
                        failedAfterRetry.Add(appId);
                    }
                    else if (!manifestData.TryGetValue("UpdateResult", out string updateResultValue) ||
                             (updateResultValue != "0" && updateResultValue != "2" && updateResultValue != "23"))
                    {
                        string resultText = manifestData.TryGetValue("UpdateResult", out var ur) ? $"UpdateResult: {ur}" : "UpdateResult không tồn tại";
                        _logger.LogError($"Thử lại cho '{gameName}' (AppID: {appId}) thất bại ({resultText}).");
                        await SafeSendLogAsync(profile.Name, "Error", $"Thử lại cho '{gameName}' ({appId}) thất bại ({resultText}).");
                        failedAfterRetry.Add(appId);
                    }
                    else
                    {
                        _logger.LogInformation($"Thử lại cho '{gameName}' (AppID: {appId}) thành công (UpdateResult: {updateResultValue}).");
                        await SafeSendLogAsync(profile.Name, "Success", $"Thử lại cho '{gameName}' ({appId}) thành công (UpdateResult: {updateResultValue}).");
                    }
                }
                retryRunSuccessful = !failedAfterRetry.Any(); // Retry successful if no apps failed *after* the retry validation check
            }


            // 6. Determine Overall Success & Update Profile Status
            bool overallSuccess = failedAppIdsForRetry.Count == 0 || retryRunSuccessful;

            profile.Status = overallSuccess ? "Stopped" : "Error"; // Use "Error" status for failure
            profile.StopTime = DateTime.Now;
            profile.Pid = 0; // Clear PID after process completion/kill
            profile.LastRun = DateTime.UtcNow; // Record last run time
            await _profileService.UpdateProfile(profile);

            if (overallSuccess)
            {
                await SafeSendLogAsync(profile.Name, "Success", $"Hoàn tất cập nhật {profile.Name}.");
            }
            else
            {
                // Log specific failed apps if possible
                var finalFailedNames = (failedAppIdsForRetry.Count > 0 && !retryRunSuccessful)
                    ? failedAppIdsForRetry // If retry itself failed or wasn't fully successful use the list from retry check
                    : failedAppIdsForRetry; // Fallback to initial failed list if retry wasn't needed but something went wrong conceptually

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

            return overallSuccess;
        }

        /// <summary>
        /// Helper to get game names for a list of App IDs. Prioritizes manifest, falls back to API.
        /// NOTE: This helper is less critical now that UpdateCheckService handles core update logic.
        /// It's kept here for logging within SteamCmdService's execution flow.
        /// </summary>
        private async Task<Dictionary<string, string>> GetAppNamesAsync(string steamappsDir, List<string> appIds)
        {
            var appNames = new Dictionary<string, string>();
            foreach (var appId in appIds)
            {
                string gameName = appId; // Default
                var manifestData = await ReadAppManifest(steamappsDir, appId); // Use the now public ReadAppManifest
                if (manifestData != null && manifestData.TryGetValue("name", out var nameValue) && !string.IsNullOrWhiteSpace(nameValue))
                {
                    gameName = nameValue;
                }
                else
                {
                    // Fallback to API only if manifest name is missing/empty
                    var appInfo = await _steamApiService.GetAppUpdateInfo(appId); // Use injected SteamApiService
                    if (appInfo != null && !string.IsNullOrEmpty(appInfo.Name))
                    {
                        gameName = appInfo.Name;
                    }
                    // Small delay to avoid hammering the API if many names are missing from manifests
                    await Task.Delay(200);
                }
                appNames[appId] = gameName;
            }
            return appNames;
        }


        /// <summary>
        /// Result of a single SteamCMD process execution.
        /// </summary>
        private class SteamCmdRunResult
        {
            public bool Success { get; set; } // Based on Exit Code (0 or 2)
            // FailedAppIdsState204 is removed as primary check relies on post-run manifest parsing
            public int ExitCode { get; set; }
        }

        /// <summary>
        /// Runs the SteamCMD process for the specified App IDs.
        /// </summary>
        /// <param name="forceValidate">Append 'validate' after each 'app_update'.</param>
        /// <returns>Result including exit code.</returns>
        private async Task<SteamCmdRunResult> RunSteamCmdProcessAsync(SteamCmdProfile profile, int profileId, List<string> appIdsToUpdate, bool forceValidate)
        {
            Process steamCmdProcess = null;
            var runResult = new SteamCmdRunResult { Success = false, ExitCode = -1 };
            string steamCmdPath = GetSteamCmdPath();
            string steamCmdDir = Path.GetDirectoryName(steamCmdPath);

            // Regex for detecting 0x204 error during runtime - kept for immediate logging but not primary failure detection
            var errorRegexState204 = new Regex(@"Error! App '(\d+)' state is 0x204 after update job", RegexOptions.Compiled | RegexOptions.IgnoreCase);

            try
            {
                if (!File.Exists(steamCmdPath))
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại: {steamCmdPath}");
                    return runResult;
                }

                // --- Prepare SteamCMD Arguments ---
                StringBuilder argumentsBuilder = new StringBuilder();

                // Login
                string loginCommand = "+login anonymous";
                if (!string.IsNullOrEmpty(profile.SteamUsername) && !string.IsNullOrEmpty(profile.SteamPassword))
                {
                    try
                    {
                        string username = _encryptionService.Decrypt(profile.SteamUsername);
                        string password = _encryptionService.Decrypt(profile.SteamPassword);
                        loginCommand = $"+login \"{username}\" \"{password}\""; // Quote username/password
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi giải mã thông tin đăng nhập cho profile {ProfileName}", profile.Name);
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi giải mã thông tin đăng nhập: {ex.Message}. Sử dụng login anonymous.");
                        // Fallback to anonymous login on decryption error
                        loginCommand = "+login anonymous";
                    }
                }
                argumentsBuilder.Append(loginCommand);

                // App Updates
                if (appIdsToUpdate == null || !appIdsToUpdate.Any())
                {
                    _logger.LogWarning("RunSteamCmdProcessAsync được gọi không có App ID nào để cập nhật cho profile {ProfileName}", profile.Name);
                    await SafeSendLogAsync(profile.Name, "Warning", "Không có App ID nào được chỉ định để cập nhật trong lần chạy này.");
                    runResult.Success = true; // No work to do is considered success
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

                // Custom Arguments + Quit
                if (!string.IsNullOrEmpty(profile.Arguments))
                {
                    argumentsBuilder.Append($" {profile.Arguments.Trim()}");
                }
                argumentsBuilder.Append(" +quit");

                string arguments = argumentsBuilder.ToString();
                string safeArguments = Regex.Replace(arguments, @"\+login\s+\S+\s+\S+", "+login [credentials]"); // Mask credentials
                _logger.LogInformation("Chạy SteamCMD cho '{ProfileName}' với tham số: {SafeArguments}", profile.Name, safeArguments);
                // Specific "Starting update for games..." log is now handled in ExecuteProfileUpdateAsync before calling this

                // --- Setup and Start Process ---
                steamCmdProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = steamCmdPath,
                        Arguments = arguments,
                        WorkingDirectory = steamCmdDir, // CRITICAL: Run from steamcmd directory
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true, // May be needed for some prompts
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8, // Use UTF8 for wider character support
                        StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };

                // --- Output Handling (Buffering and Throttling) ---
                var outputBuffer = new StringBuilder();
                var recentOutputMessages = new ConcurrentDictionary<string, byte>(); // Track recent messages to reduce duplicates sent to hub
                int maxRecentMessages = 50; // Limit cache size
                System.Timers.Timer outputTimer = null;

                // Function to send buffered output
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
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", outputToSend.TrimEnd()); // Send trimmed output
                    }
                }

                outputTimer = new System.Timers.Timer(250); // Send updates every 250ms
                outputTimer.Elapsed += async (sender, e) => await SendBufferedOutput();
                outputTimer.AutoReset = true;

                // Data Received Handlers
                steamCmdProcess.OutputDataReceived += (sender, e) => {
                    if (e.Data == null) return; // Ignore null data (often sent when stream closes)
                    string line = e.Data.Trim();
                    if (string.IsNullOrEmpty(line)) return;

                    // Minimal duplicate check for hub messages
                    if (recentOutputMessages.TryAdd(line, 0)) // TryAdd is atomic
                    {
                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(line);
                        }
                        // Clean up old messages if cache gets too big
                        if (recentOutputMessages.Count > maxRecentMessages * 1.5) // Clean slightly above max
                        {
                            var keysToRemove = recentOutputMessages.Keys.Take(recentOutputMessages.Count - maxRecentMessages).ToList();
                            foreach (var key in keysToRemove) recentOutputMessages.TryRemove(key, out _);
                        }

                        // Check for 0x204 error in real-time for immediate logging
                        var match = errorRegexState204.Match(line);
                        if (match.Success)
                        {
                            string failedAppId = match.Groups[1].Value;
                            _logger.LogError($"Phát hiện lỗi cập nhật thời gian thực cho App ID {failedAppId} (trạng thái 0x204) trong log của profile '{profile.Name}'.");
                            // Get name async and log - Fire and forget style to not block output handling
                            _ = Task.Run(async () => {
                                // Use SteamApiService to get the name
                                var appInfo = await _steamApiService.GetAppUpdateInfo(failedAppId);
                                string gameName = appInfo?.Name ?? failedAppId;
                                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi thời gian thực: Cập nhật thất bại '{gameName}' ({failedAppId}, 0x204).");
                            });
                        }
                    }
                };

                steamCmdProcess.ErrorDataReceived += (sender, e) => {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    _logger.LogError("SteamCMD Error ({ProfileName}): {Data}", profile.Name, e.Data);
                    // Send errors immediately without buffering/duplicate check
                    _ = _hubContext.Clients.All.SendAsync("ReceiveLog", $"LỖI SteamCMD: {e.Data}"); // Fire and forget
                };

                // --- Start and Wait ---
                _steamCmdProcesses[profileId] = steamCmdProcess; // Track the process
                steamCmdProcess.Start();
                profile.Pid = steamCmdProcess.Id; // Update PID in profile immediately after start
                await _profileService.UpdateProfile(profile);

                steamCmdProcess.BeginOutputReadLine();
                steamCmdProcess.BeginErrorReadLine();
                outputTimer.Start(); // Start sending buffered output

                // Wait for exit (consider a timeout? WaitForExit can block indefinitely)
                // bool exited = steamCmdProcess.WaitForExit(ProcessExitTimeoutMs * 10); // Example: 10x timeout for the entire process run
                steamCmdProcess.WaitForExit(); // Using indefinite wait as per original logic
                                               // if (!exited)
                                               // {
                                               //     _logger.LogWarning("SteamCMD process for profile {ProfileName} did not exit within the timeout. Attempting to kill.", profile.Name);
                                               //     await KillProcessAsync(steamCmdProcess, profile.Name);
                                               //     runResult.ExitCode = -99; // Indicate timeout exit code
                                               // }
                                               // else
                                               // {
                runResult.ExitCode = steamCmdProcess.ExitCode;
                // }

                // --- Cleanup ---
                outputTimer.Stop(); // Stop the timer
                await SendBufferedOutput(); // Send any remaining output

                _steamCmdProcesses.TryRemove(profileId, out _); // Untrack the process


                // Evaluate Success based on Exit Code (0=OK, 2=OK with info/warning)
                runResult.Success = (runResult.ExitCode == 0 || runResult.ExitCode == 2);

                if (runResult.Success)
                {
                    _logger.LogInformation("Quá trình SteamCMD cho '{ProfileName}' hoàn tất. Exit Code: {ExitCode}", profile.Name, runResult.ExitCode);
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Quá trình SteamCMD cho '{profile.Name}' hoàn tất (Code: {runResult.ExitCode}).");
                }
                else
                {
                    _logger.LogError("Quá trình SteamCMD cho '{ProfileName}' hoàn tất với lỗi. Exit Code: {ExitCode}", profile.Name, runResult.ExitCode);
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Quá trình SteamCMD cho '{profile.Name}' thất bại (Code: {runResult.ExitCode}).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng khi chạy SteamCMD cho profile {ProfileName}: {Message}", profile?.Name ?? "Unknown", ex.Message);
                await SafeSendLogAsync(profile?.Name ?? $"Profile {profileId}", "Error", $"Lỗi nghiêm trọng khi chạy SteamCMD: {ex.Message}");
                runResult.Success = false;
                runResult.ExitCode = -1; // Indicate external error
            }
            finally
            {
                // Ensure process is disposed
                steamCmdProcess?.Dispose();
                // outputTimer?.Dispose(); // Dispose timer if it was created
            }

            return runResult;
        }


        /// <summary>
        /// Reads and parses an appmanifest_XXXXXX.acf file using robust regex.
        /// Made public to be accessible by UpdateCheckService.
        /// </summary>
        /// <returns>Dictionary of key-value pairs or null on error/not found.</returns>
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

                // Regex cải tiến để bắt các giá trị trong ngoặc kép, kể cả giá trị có khoảng trắng và ký tự đặc biệt
                var regex = new Regex(@"""(?<key>[^""]+)""\s+""(?<value>[^""]*)""", RegexOptions.Compiled);
                var matches = regex.Matches(content);

                foreach (Match match in matches)
                {
                    if (match.Success)
                    {
                        manifestData[match.Groups["key"].Value] = match.Groups["value"].Value;
                    }
                }

                // Thêm phần đọc SizeOnDisk nếu không có
                if (!manifestData.ContainsKey("SizeOnDisk"))
                {
                    // Tìm trong các giá trị "size" hoặc "installsize" nếu có
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

        // --- Run/Stop All, Restart, Shutdown ---

        public async Task RunAllProfilesAsync()
        {
            // Robust check to prevent re-entry
            if (_isRunningAllProfiles)
            {
                _logger.LogWarning("RunAllProfilesAsync called but already running.");
                await SafeSendLogAsync("System", "Warning", "Đang có quá trình chạy tất cả profile, không thể thực hiện yêu cầu này.");
                return;
            }

            _isRunningAllProfiles = true; // Set flag EARLY

            try // Add try block
            {
                var profiles = await _profileService.GetAllProfiles();
                if (!profiles.Any())
                {
                    await SafeSendLogAsync("System", "Warning", "Không có cấu hình nào để chạy.");
                    return; // Exit early if no profiles
                }

                // Reset cancel flag specifically for this run
                _cancelAutoRun = false;
                _currentProfileIndex = 0; // Reset index

                _logger.LogInformation("RunAllProfilesAsync: Bắt đầu thêm tất cả profile vào hàng đợi cập nhật..."); // Changed log message slightly for clarity
                await SafeSendLogAsync("System", "Info", "Bắt đầu thêm tất cả profile vào hàng đợi cập nhật...");

                // Ensure a clean state before queueing all
                _logger.LogInformation("RunAllProfilesAsync: Calling StopAllProfilesAsync as preparation..."); // Added log
                await StopAllProfilesAsync(); // Stop current + kill all steamcmd
                _logger.LogInformation("RunAllProfilesAsync: StopAllProfilesAsync preparation completed. Waiting {Delay}ms...", RetryDelayMs); // Added log
                await Task.Delay(RetryDelayMs);

                // Queue each profile
                _logger.LogInformation("RunAllProfilesAsync: Starting profile queueing loop..."); // Added log
                foreach (var profile in profiles)
                {
                    // The user must trigger cancellation elsewhere now.
                    // If you need a way to cancel this loop, you'll need a separate method
                    // that sets _cancelAutoRun = true, triggered by UI/API.
                    // if (_cancelAutoRun) // Keep check if explicit cancel is needed
                    // {
                    //      await SafeSendLogAsync("System", "Info", "Đã hủy quá trình chạy tất cả profile.");
                    //      break;
                    // }

                    _currentProfileIndex++;
                    await SafeSendLogAsync("System", "Info", $"Đang thêm profile ({_currentProfileIndex}/{profiles.Count}): {profile.Name} vào hàng đợi...");
                    await QueueProfileForUpdate(profile.Id); // Queue it
                    await Task.Delay(500); // Small delay between queueing
                }
                _logger.LogInformation("RunAllProfilesAsync: Finished profile queueing loop."); // Added log


                // Removed the check for _cancelAutoRun here, success depends on queueing completion
                await SafeSendLogAsync("System", "Info", "Đã thêm tất cả profile vào hàng đợi. Bộ xử lý hàng đợi sẽ chạy chúng tuần tự.");

            }
            catch (Exception ex) // Catch potential errors during setup/queueing
            {
                _logger.LogError(ex, "Lỗi trong RunAllProfilesAsync.");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chuẩn bị chạy tất cả profile: {ex.Message}");
            }
            finally // Add finally block
            {
                _logger.LogInformation("RunAllProfilesAsync: Resetting _isRunningAllProfiles = false."); // Added log
                _isRunningAllProfiles = false; // Ensure flag is always reset
            }
        }

        public async Task StopAllProfilesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các profile và xóa hàng đợi...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profile và xóa hàng đợi...");

            // 1. Clear the update queue
            int clearedCount = 0;
            while (_updateQueue.TryDequeue(out _)) { clearedCount++; }
            if (clearedCount > 0) _logger.LogInformation("Đã xóa {Count} mục khỏi hàng đợi cập nhật.", clearedCount);

            // 2. Set flags to stop ongoing loops/processes
            // _cancelAutoRun = true; // <<<<<<< REMOVE THIS LINE >>>>>>>>>>>>
            _isRunningAllProfiles = false; // Indicate RunAll is not active (This might still be needed here or in RunAllProfiles finally block)

            // 3. Kill all tracked and untracked SteamCMD processes
            await KillAllSteamCmdProcessesAsync();
            await Task.Delay(1000); // Short wait after killing

            // 4. Update status of any profiles that were 'Running'
            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.Status == "Running"))
            {
                _logger.LogInformation("Cập nhật trạng thái profile '{ProfileName}' thành Stopped do StopAll.", profile.Name);
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0; // Clear PID
                await _profileService.UpdateProfile(profile);
            }

            await SafeSendLogAsync("System", "Success", "Đã dừng tất cả các profile và xóa hàng đợi.");
        }

        /// <summary>
        /// Restarts a specific profile by stopping it (if running) and re-queueing it.
        /// </summary>
        public async Task<bool> RestartProfileAsync(int profileId)
        {
            var profile = await _profileService.GetProfileById(profileId);
            if (profile == null) return false;

            await SafeSendLogAsync(profile.Name, "Info", $"Đang khởi động lại profile {profile.Name}...");

            // Stop the specific process if tracked
            if (_steamCmdProcesses.TryRemove(profileId, out var process))
            {
                await KillProcessAsync(process, profile.Name);
                process.Dispose();
            }
            // Also ensure any untracked process with the same logic is killed (redundant but safe)
            // Consider finding process by PID if stored reliably in profile, otherwise KillAll is needed.
            // For simplicity, relying on queue processor's StopAll before next run.

            await Task.Delay(RetryDelayMs); // Wait before re-queueing

            // Re-queue the profile
            return await QueueProfileForUpdate(profileId);
        }

        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang tắt dịch vụ SteamCMD...");
            await SafeSendLogAsync("System", "Info", "Đang tắt ứng dụng...");

            // Stop timers
            _scheduleTimer?.Stop();
            _scheduleTimer?.Dispose();
            _hubMessageCleanupTimer?.Stop();
            _hubMessageCleanupTimer?.Dispose();

            // Stop all profiles and clear queue
            await StopAllProfilesAsync();

            _logger.LogInformation("Đã hoàn thành tắt dịch vụ SteamCMD.");
            await SafeSendLogAsync("System", "Success", "Đã hoàn thành dừng process.");
            // Note: Actual application shutdown (e.g., Environment.Exit) is handled elsewhere
        }
    } // End class SteamCmdService
} // End namespace SteamCmdWebAPI.Services
#endregion
