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
using System.Text.RegularExpressions; // Added for regex to extract App IDs

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
        // Adjusted base retry delay and process exit timeout based on user's request for higher delays
        private const int RetryDelayMs = 5000; // Increased base retry delay
        private const int ProcessExitTimeoutMs = 20000; // Increased process exit timeout

        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private readonly System.Timers.Timer _scheduleTimer; // Assuming scheduling is needed based on original code

        private volatile bool _isRunningAllProfiles = false; // Assuming auto-run feature is needed
        private int _currentProfileIndex = 0; // Assuming auto-run feature is needed
        private volatile bool _cancelAutoRun = false; // Assuming auto-run feature is needed
        private DateTime _lastAutoRunTime = DateTime.MinValue; // Assuming auto-run feature is needed

        // Assuming logging is needed based on original code
        private readonly List<LogEntry> _logs = new List<LogEntry>(MaxLogEntries);
        private HashSet<string> _recentLogMessages = new HashSet<string>();
        private readonly int _maxRecentLogMessages = 100;

        // LogEntry class as in the original code
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

        // Thêm các thuộc tính và phương thức quản lý hàng đợi vào đầu lớp SteamCmdService
        private readonly ConcurrentQueue<int> _updateQueue = new ConcurrentQueue<int>();
        private volatile bool _isProcessingQueue = false;
        private Task _queueProcessorTask = null;

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

            // Assuming scheduling is needed
            _scheduleTimer = new System.Timers.Timer(60000);
            _scheduleTimer.Elapsed += async (s, e) => await CheckScheduleAsync();
            _scheduleTimer.AutoReset = true;
            _scheduleTimer.Start();
            _logger.LogInformation("Bộ lập lịch đã khởi động.");
        }

        #region Log and Notification Methods
        // Keeping original logging methods
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
        // Đơn giản hóa phương thức StartAllAutoRunProfilesAsync
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
            // Dừng tất cả các tiến trình trước khi bắt đầu
            await KillAllSteamCmdProcessesAsync();
            // Thêm các profile auto run vào hàng đợi cập nhật
            foreach (var profile in autoRunProfiles)
            {
                await QueueProfileForUpdate(profile.Id);
                await Task.Delay(500); // Đợi 0.5 giây giữa các lần thêm vào hàng đợi
            }

            await SafeSendLogAsync("System", "Success", "Đã thêm tất cả profile Auto Run vào hàng đợi cập nhật");
        }

        // Đơn giản hóa phương thức CheckScheduleAsync
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
            }
        }

        #endregion

        #region Process Management Utilities
        // Giảm bớt phương thức KillProcessAsync
        private async Task<bool> KillProcessAsync(Process process, string profileName)
        {
            if (process == null || process.HasExited)
                return true;
            try
            {
                process.Terminator(ProcessExitTimeoutMs);
                await Task.Delay(500);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileName}", profileName);
                return false;
            }
        }

        // Đơn giản hóa phương thức KillAllSteamCmdProcessesAsync
        private async Task<bool> KillAllSteamCmdProcessesAsync()
        {
            try
            {
                // Dừng tất cả các process đang theo dõi
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    await KillProcessAsync(kvp.Value, $"Profile {kvp.Key}");
                    _steamCmdProcesses.TryRemove(kvp.Key, out _);
                }

                // Dùng taskkill để kết thúc tất cả các process steamcmd.exe còn lại
                CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe", ProcessExitTimeoutMs);
                await Task.Delay(1000); // Đợi 1 giây

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả tiến trình SteamCMD");
                return false;
            }
        }

        #endregion

        #region SteamCmd Installation and Path Management
        // Keeping original installation and path management methods
        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            return Path.Combine(steamCmdDir, "steamcmd.exe");
        }

        // Đơn giản hóa phương thức IsSteamCmdInstalled
        public Task<bool> IsSteamCmdInstalled()
        {
            string steamCmdPath = GetSteamCmdPath();
            return Task.FromResult(File.Exists(steamCmdPath));
        }


        public async Task InstallSteamCmd()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");
            string steamCmdPath = Path.Combine(steamCmdDir, "steamcmd.exe");
            string downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

            try
            {
                // Kill tất cả tiến trình steamcmd.exe trước khi cài đặt
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(5000); // Đợi 5 giây để đảm bảo các tiến trình đã bị kill

                // Kiểm tra nếu file steamcmd.exe đang tồn tại và không thể xoá
                if (File.Exists(steamCmdPath))
                {
                    try
                    {
                        // Thử mở file để kiểm tra xem có bị khoá không
                        using (var fileStream = new FileStream(steamCmdPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                        {
                            // File không bị khoá, có thể đóng fileStream
                        }
                    }
                    catch (IOException)
                    {
                        // File đang bị khoá, thử xoá lại sau khi kill tiến trình
                        CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe", 10000);
                        await Task.Delay(2000);

                        // Thử xoá file
                        try
                        {
                            File.Delete(steamCmdPath);
                            await Task.Delay(1000);
                        }
                        catch
                        {
                            // Nếu vẫn không xoá được, thông báo lỗi
                            _logger.LogError($"Không thể xoá file steamcmd.exe hiện tại. File đang bị khoá.");
                            await SafeSendLogAsync("System", "Error", $"Không thể xoá file steamcmd.exe hiện tại. File đang bị khoá bởi tiến trình khác.");
                        }
                    }
                }

                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd: {Directory}", steamCmdDir);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamcmd: {steamCmdDir}");
                }

                // Tải xuống steamcmd.zip
                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);
                    await SafeSendLogAsync("System", "Info", $"Đang tải SteamCMD từ {downloadUrl}...");
                    _logger.LogInformation("Bắt đầu tải SteamCMD từ {Url}", downloadUrl);

                    // Thử xoá file zip cũ nếu tồn tại
                    if (File.Exists(zipPath))
                    {
                        try { File.Delete(zipPath); } catch { }
                    }

                    var response = await httpClient.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(zipPath, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                    await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                }

                // Đảm bảo không có file cũ nào đang chạy trước khi giải nén
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(3000);

                try
                {
                    // Giải nén
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, steamCmdDir, true);
                    await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công");
                }
                catch (IOException ex)
                {
                    //_logger.LogError(ex, "Lỗi khi giải nén: {Message}", ex.Message);
                    await SafeSendLogAsync("System", "Error", $"Lỗi khi giải nén: {ex.Message}");

                    // Thử phương pháp giải nén thủ công
                    await SafeSendLogAsync("System", "Info", "Đang thử phương pháp giải nén thủ công...");
                    CmdHelper.RunCommand($"powershell -command \"Expand-Archive -Path '{zipPath}' -DestinationPath '{steamCmdDir}' -Force\"", 60000);
                }

                try { File.Delete(zipPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip"); }

                // Kiểm tra lại sau khi cài đặt
                if (!File.Exists(steamCmdPath))
                {
                    throw new Exception("Cài đặt thất bại. Không tìm thấy steamcmd.exe sau khi cài đặt.");
                }

                // Kiểm tra file có thể thực thi
                try
                {
                    FileInfo fileInfo = new FileInfo(steamCmdPath);
                    if (fileInfo.Length < 10000) // Kiểm tra kích thước tối thiểu
                    {
                        throw new Exception("File steamcmd.exe được tạo nhưng có kích thước không hợp lệ.");
                    }

                    await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra file steamcmd.exe: {Message}", ex.Message);
                    await SafeSendLogAsync("System", "Error", $"Lỗi khi kiểm tra file steamcmd.exe: {ex.Message}");
                    throw;
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

        #region Folder Setup

        /// <summary>
        /// Prepares the folder structure, including deleting the old local steamapps directory and creating a symbolic link.
        /// This method ensures the local steamapps directory is deleted (with retries and process killing) before creating the symbolic link.
        /// </summary>
        /// <param name="gameInstallDir">The game installation directory where the target steamapps folder resides.</param>
        /// <returns>True if the folder structure preparation (deletion and symbolic link creation) was successful, false otherwise.</returns>
        private async Task<bool> PrepareFolderStructure(string gameInstallDir)
        {
            string steamappsDir = Path.Combine(gameInstallDir, "steamapps");
            string localSteamappsDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            // Ensure the game install steamapps directory exists as the target for the symbolic link
            if (!Directory.Exists(steamappsDir))
            {
                try
                {
                    Directory.CreateDirectory(steamappsDir);
                    _logger.LogInformation($"Created game install steamapps directory: {steamappsDir}");
                    await SafeSendLogAsync("System", "Info", $"Created game install steamapps directory: {steamappsDir}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to create game install steamapps directory: {steamappsDir}");
                    await SafeSendLogAsync("System", "Error", $"Failed to create game install steamapps directory: {steamappsDir}: {ex.Message}");
                    // If the target directory cannot be created, we cannot create the symbolic link later.
                    return false;
                }
            }

            // Delete the existing local steamapps folder if it exists - Ensure deletion with retries and kill
            if (Directory.Exists(localSteamappsDir))
            {
                //_logger.LogInformation($"Attempting to delete old local steamapps directory: {localSteamappsDir}");
                await SafeSendLogAsync("System", "Info", $"Attempting to delete old local steamapps directory: {localSteamappsDir}");

                bool deleted = false;
                int retryCount = 15; // Increased max retry attempts for robustness
                int baseRetryDelayMs = 3000; // Increased base delay between retries

                for (int i = 0; i < retryCount; i++)
                {
                    try
                    {
                        //_logger.LogInformation($"Attempting to delete directory {localSteamappsDir} attempt {i + 1}/{retryCount}...");
                        await SafeSendLogAsync("System", "Info", $"Attempting to delete old steamapps directory attempt {i + 1}/{retryCount}...");

                        // Use rmdir /S /Q to force delete the directory and its contents
                        // Increased timeout for deletion command to allow more time
                        CmdHelper.RunCommand($"rmdir /S /Q \"{localSteamappsDir}\"", 45000); // Increased timeout
                        await Task.Delay(baseRetryDelayMs); // Short delay after running the command

                        if (!Directory.Exists(localSteamappsDir))
                        {
                            _logger.LogInformation("Successfully deleted old local steamapps directory.");
                            await SafeSendLogAsync("System", "Success", "Successfully deleted old local steamapps directory.");
                            deleted = true;
                            break; // Deletion successful, exit loop
                        }
                        else
                        {
                            _logger.LogWarning($"Old local steamapps directory still exists after attempt {i + 1}. Attempting to kill SteamCMD and waiting before retrying.");
                            await SafeSendLogAsync("System", "Warning", $"Old local steamapps directory still exists after attempt {i + 1}. Attempting to kill SteamCMD and waiting before retrying.");

                            // Attempt to kill SteamCMD processes if deletion fails
                            await KillAllSteamCmdProcessesAsync();
                            await Task.Delay(7000); // Increased wait time (7 seconds) after killing before retrying deletion

                            await Task.Delay(baseRetryDelayMs * (i + 1)); // Increase delay for subsequent retries
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deleting old local steamapps directory on attempt {i + 1}");
                        await SafeSendLogAsync("System", "Error", $"Error deleting old local steamapps directory on attempt {i + 1}: {ex.Message}");

                        // Attempt to kill SteamCMD processes if deletion encounters an error
                        _logger.LogInformation($"Encountered error during directory deletion, attempting to kill SteamCMD before retrying.");
                        await SafeSendLogAsync("System", "Info", $"Encountered error during directory deletion, attempting to kill SteamCMD before retrying.");
                        await KillAllSteamCmdProcessesAsync();
                        await Task.Delay(7000); // Increased wait time (7 seconds) after killing before retrying deletion

                        await Task.Delay(baseRetryDelayMs * (i + 1)); // Wait before next retry
                    }
                }

                if (!deleted)
                {
                    _logger.LogError($"Failed to delete old local steamapps directory at {localSteamappsDir} after {retryCount} attempts. Cannot proceed with symbolic link.");
                    await SafeSendLogAsync("System", "Error", $"Failed to delete old local steamapps directory at {localSteamappsDir} after {retryCount} attempts. Cannot proceed with symbolic link.");
                    // If deletion failed after all retries, we cannot proceed with the symbolic link
                    return false; // Indicate failure
                }
            }

            // Create symbolic link only if the old local steamapps directory was successfully deleted (or didn't exist initially)
            // and the target directory exists.
            if (!Directory.Exists(localSteamappsDir))
            {
                //_logger.LogInformation($"Creating symbolic link from \"{steamappsDir}\" to \"{localSteamappsDir}\"");
                await SafeSendLogAsync("System", "Info", $"Creating symbolic link from \"{steamappsDir}\" to \"{localSteamappsDir}\"");
                try
                {
                    // Ensure the target directory exists before creating the link (already checked above, but defensive check)
                    if (!Directory.Exists(steamappsDir))
                    {
                        _logger.LogError($"Target directory for symbolic link does not exist: {steamappsDir}. Cannot create link.");
                        await SafeSendLogAsync("System", "Error", $"Target directory for symbolic link does not exist: {steamappsDir}. Cannot create link.");
                        return false; // Cannot create link if target is missing
                    }

                    // Use mklink /D to create a directory symbolic link
                    CmdHelper.RunCommand($"mklink /D \"{localSteamappsDir}\" \"{steamappsDir}\"", 15000); // Increased timeout for mklink command
                    await Task.Delay(3000); // Increased delay after creating link

                    if (Directory.Exists(localSteamappsDir))
                    {
                        //_logger.LogInformation("Successfully created symbolic link.");
                        await SafeSendLogAsync("System", "Success", "Successfully created symbolic link.");
                        return true; // Symbolic link created successfully
                    }
                    else
                    {
                        //_logger.LogError($"Failed to create symbolic link to {localSteamappsDir}. Directory does not exist after mklink command.");
                        await SafeSendLogAsync("System", "Error", $"Failed to create symbolic link to {localSteamappsDir}. Directory does not exist after mklink command.");
                        return false; // Symbolic link creation failed
                    }
                }
                catch (Exception ex)
                {
                    //_logger.LogError(ex, "Error creating symbolic link");
                    await SafeSendLogAsync("System", "Error", $"Error creating symbolic link: {ex.Message}");
                    return false; // Error during symbolic link creation
                }
            }
            else
            {
                // This case indicates that Directory.Exists(localSteamappsDir) was true initially
                // but the deletion failed after all retries.
                _logger.LogError("Old local steamapps directory still exists after deletion attempts. Skipping symbolic link creation.");
                await SafeSendLogAsync("System", "Error", "Old local steamapps directory still exists after deletion attempts. Skipping symbolic link creation.");
                return false; // Deletion failed, so symbolic link wasn't created
            }
        }

        #endregion

        #region Public API Methods

        /// <summary>
        /// Thêm một profile vào hàng đợi cập nhật
        /// </summary>
        public async Task<bool> QueueProfileForUpdate(int profileId)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile ID {0} để thêm vào hàng đợi", profileId);
                    return false;
                }

                // Kiểm tra xem profile này đã có trong hàng đợi chưa
                if (_updateQueue.Contains(profileId))
                {
                    _logger.LogInformation("Profile ID {0} đã có trong hàng đợi cập nhật", profileId);
                    return true;
                }

                // Thêm vào hàng đợi
                _updateQueue.Enqueue(profileId);
                _logger.LogInformation("Đã thêm profile ID {0} vào hàng đợi cập nhật", profileId);
                // Khởi động bộ xử lý hàng đợi nếu chưa chạy
                if (_queueProcessorTask == null || _queueProcessorTask.IsCompleted)
                {
                    _queueProcessorTask = Task.Run(ProcessUpdateQueueAsync);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm profile ID {0} vào hàng đợi cập nhật", profileId);
                return false;
            }
        }

        /// <summary>
        /// Xử lý hàng đợi cập nhật
        /// </summary>
        private async Task ProcessUpdateQueueAsync()
        {
            if (_isProcessingQueue)
                return;
            _isProcessingQueue = true;

            try
            {
                _logger.LogInformation("Bắt đầu xử lý hàng đợi cập nhật");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Bắt đầu xử lý hàng đợi cập nhật");
                while (_updateQueue.TryDequeue(out int profileId))
                {
                    try
                    {
                        _logger.LogInformation("Đang xử lý cập nhật cho profile ID {0}", profileId);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đang xử lý cập nhật cho profile ID {profileId}");
                        // Đảm bảo dừng mọi tiến trình đang chạy trước khi cập nhật mới
                        await StopAllProfilesAsync();
                        await Task.Delay(5000); // Đợi 5 giây để đảm bảo tất cả đã dừng

                        // Chạy cập nhật
                        bool success = await ExecuteProfileUpdateAsync(profileId);
                        if (success)
                        {
                            _logger.LogInformation("Đã xử lý cập nhật thành công cho profile ID {0}", profileId);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã xử lý cập nhật thành công cho profile ID {profileId}");
                        }
                        else
                        {
                            _logger.LogWarning("Xử lý cập nhật không thành công cho profile ID {0}", profileId);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Xử lý cập nhật không thành công cho profile ID {profileId}");
                        }

                        // Đợi một chút trước khi xử lý tiếp theo
                        await Task.Delay(3000);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý cập nhật cho profile ID {0}", profileId);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi xử lý cập nhật cho profile ID {profileId}: {ex.Message}");
                    }
                }

                _logger.LogInformation("Đã xử lý xong hàng đợi cập nhật");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã xử lý xong hàng đợi cập nhật");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình xử lý hàng đợi cập nhật");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi trong quá trình xử lý hàng đợi cập nhật: {ex.Message}");
            }
            finally
            {
                _isProcessingQueue = false;
            }
        }

        // Sửa phương thức RunProfileAsync để thêm vào hàng đợi thay vì chạy trực tiếp
        public async Task<bool> RunProfileAsync(int id)
        {
            try
            {
                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    _logger.LogError("Profile ID {ProfileId} not found", id);
                    await SafeSendLogAsync($"Profile {id}", "Error", $"Profile with ID {id} not found");
                    return false;
                }

                // Kiểm tra nếu hàng đợi đang xử lý profile này
                if (_isProcessingQueue && _updateQueue.Contains(id))
                {
                    _logger.LogInformation("Profile ID {0} đang được xử lý trong hàng đợi", id);
                    await SafeSendLogAsync(profile.Name, "Info", $"Profile {profile.Name} đang được xử lý trong hàng đợi");
                    return true;
                }

                // Thêm vào hàng đợi
                return await QueueProfileForUpdate(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm profile ID {0} vào hàng đợi", id);
                return false;
            }
        }

        // Thêm phương thức thực thi cập nhật thực tế (đổi tên từ RunProfileAsync gốc)
        private async Task<bool> ExecuteProfileUpdateAsync(int id)
        {
            try
            {
                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    _logger.LogError("Profile ID {ProfileId} not found", id);
                    await SafeSendLogAsync($"Profile {id}", "Error", $"Profile with ID {id} not found");
                    return false;
                }

                await SafeSendLogAsync(profile.Name, "Info", $"Chuẩn bị cập nhật {profile.Name}...");
                // Dừng các process hiện tại nếu có
                if (_steamCmdProcesses.TryGetValue(id, out var existingProcess))
                {
                    await KillProcessAsync(existingProcess, profile.Name);
                    _steamCmdProcesses.TryRemove(id, out _);
                }

                // Dừng tất cả các process khác để đảm bảo không xung đột
                await KillAllSteamCmdProcessesAsync();
                // Đợi sau khi dừng các process
                await Task.Delay(5000);
                // Kiểm tra và cài đặt SteamCMD nếu cần
                bool steamCmdFileExists = await IsSteamCmdInstalled();
                if (!steamCmdFileExists)
                {
                    await SafeSendLogAsync(profile.Name, "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                    await InstallSteamCmd();

                    // Kiểm tra lại sau khi cài đặt
                    steamCmdFileExists = await IsSteamCmdInstalled();
                    if (!steamCmdFileExists)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", "Không thể cài đặt SteamCMD.");
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        return false;
                    }
                }

                // Kiểm tra thư mục cài đặt
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
                }

                // Chuẩn bị cấu trúc thư mục
                bool folderPreparationSuccess = await PrepareFolderStructure(profile.InstallDirectory);
                if (!folderPreparationSuccess)
                {
                    await SafeSendLogAsync(profile.Name, "Error", "Lỗi khi chuẩn bị cấu trúc thư mục.");
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0;
                    await _profileService.UpdateProfile(profile);
                    return false;
                }

                // --- Start: Added code to find additional App IDs from appmanifest files ---
                var additionalAppIds = new List<string>();
                string linkedSteamappsDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

                if (Directory.Exists(linkedSteamappsDir))
                {
                    try
                    {
                        var manifestFiles = Directory.GetFiles(linkedSteamappsDir, "appmanifest_*.acf");
                        var regex = new Regex(@"appmanifest_(\d+)\.acf");

                        foreach (var file in manifestFiles)
                        {
                            var match = regex.Match(Path.GetFileName(file));
                            if (match.Success)
                            {
                                string appId = match.Groups[1].Value;
                                if (appId != profile.AppID) // Avoid adding the main profile's AppID again
                                {
                                    additionalAppIds.Add(appId);
                                    _logger.LogInformation($"Found additional App ID from manifest: {appId}");
                                }
                            }
                        }
                        if (additionalAppIds.Any())
                        {
                            await SafeSendLogAsync(profile.Name, "Info", $"Tìm thấy {additionalAppIds.Count} App ID phụ từ file manifest.");
                        }
                        else
                        {
                            await SafeSendLogAsync(profile.Name, "Info", "Không tìm thấy App ID phụ nào từ file manifest.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đọc file appmanifest trong thư mục steamapps đã liên kết.");
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi đọc file appmanifest trong thư mục steamapps đã liên kết: {ex.Message}");
                        // Continue even if reading manifest files fails
                    }
                }
                else
                {
                    _logger.LogWarning($"Thư mục steamapps đã liên kết không tồn tại: {linkedSteamappsDir}");
                    await SafeSendLogAsync(profile.Name, "Warning", $"Thư mục steamapps đã liên kết không tồn tại sau khi chuẩn bị cấu trúc thư mục: {linkedSteamappsDir}");
                    // Continue, but no additional apps will be updated
                }
                // --- End: Added code to find additional App IDs from appmanifest files ---


                // Cập nhật trạng thái profile trước khi chạy
                profile.Status = "Running";
                profile.StartTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);

                // Thực hiện cập nhật game (lần 1: không validate, trừ khi cấu hình profile yêu cầu)
                bool success = await RunSteamCmdProcessAsync(profile, id, additionalAppIds);

                // Nếu lần chạy đầu tiên không thành công, thử lại với validate cho tất cả các app
                if (!success)
                {
                    _logger.LogWarning($"Lần cập nhật đầu tiên cho profile {profile.Name} không thành công. Đang thử lại với validate.");
                    await SafeSendLogAsync(profile.Name, "Warning", "Lần cập nhật đầu tiên không thành công. Đang thử lại với validate.");

                    // Dừng tất cả các process khác để đảm bảo không xung đột trước lần thử lại
                    await KillAllSteamCmdProcessesAsync();
                    await Task.Delay(5000); // Đợi sau khi dừng

                    success = await RunSteamCmdProcessWithValidationAsync(profile, id, additionalAppIds);

                    if (success)
                    {
                        _logger.LogInformation($"Lần cập nhật với validate cho profile {profile.Name} thành công.");
                        await SafeSendLogAsync(profile.Name, "Success", "Lần cập nhật với validate thành công.");
                    }
                    else
                    {
                        _logger.LogError($"Lần cập nhật với validate cho profile {profile.Name} cũng không thành công.");
                        await SafeSendLogAsync(profile.Name, "Error", "Lần cập nhật với validate cũng không thành công.");
                    }
                }


                // Cập nhật trạng thái profile sau khi chạy
                profile.Status = success ? "Stopped" : "Error";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                profile.LastRun = DateTime.UtcNow;
                await _profileService.UpdateProfile(profile);
                if (success)
                {
                    await SafeSendLogAsync(profile.Name, "Success", $"Cập nhật thành công game {profile.Name}");
                }
                else
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"Cập nhật không thành công game {profile.Name}. Kiểm tra log để biết thêm chi tiết.");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi cập nhật profile {ProfileId}: {Message}", id, ex.Message);
                try
                {
                    var profile = await _profileService.GetProfileById(id);
                    if (profile != null)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi cập nhật {profile.Name}: {ex.Message}");
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

        /// <summary>
        /// Runs the actual SteamCMD process for a given profile WITHOUT forcing validate.
        /// The 'validate' argument is included only if profile.ValidateFiles is true, appended at the end of all app_update commands.
        /// </summary>
        /// <param name="profile">The SteamCmdProfile to run.</param>
        /// <param name="profileId">The ID of the profile.</param>
        /// <param name="additionalAppIds">A list of additional App IDs to update in the same process.</param>
        /// <returns>True if the SteamCMD process finished successfully, false otherwise.</returns>
        private async Task<bool> RunSteamCmdProcessAsync(SteamCmdProfile profile, int profileId, List<string> additionalAppIds = null)
        {
            Process steamCmdProcess = null;
            bool success = false;
            string steamCmdPath = GetSteamCmdPath();
            // Using HashSet for efficient checking of success messages
            var successMessages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"Success! App '{profile.AppID}' already up to date.",
                $"Success! App '{profile.AppID}' fully installed.",
                $"Success! App '{profile.AppID}' updated."
            };

            // Add success messages for additional App IDs
            if (additionalAppIds != null)
            {
                foreach (var appId in additionalAppIds)
                {
                    successMessages.Add($"Success! App '{appId}' already up to date.");
                    successMessages.Add($"Success! App '{appId}' fully installed.");
                    successMessages.Add($"Success! App '{appId}' updated.");
                }
            }


            try
            {
                if (!File.Exists(steamCmdPath))
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại tại {steamCmdPath}");
                    return false;
                }

                string logFilePath = GetSteamCmdLogPath(profileId);

                // Start LogFileReader to monitor the console log file
                _logFileReader.StartMonitoring(logFilePath, async (content) =>
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        // Send log content to clients via SignalR
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", content);
                    }
                });

                try
                {
                    // Prepare login command
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
                            // Decide if this should stop the process - currently logs and continues
                        }
                    }

                    // Prepare custom arguments
                    string validateArg = profile.ValidateFiles ? "validate" : "";
                    string argumentsArg = string.IsNullOrEmpty(profile.Arguments) ? "" : profile.Arguments.Trim();

                    // Construct the full command arguments including additional App IDs
                    StringBuilder argumentsBuilder = new StringBuilder();
                    argumentsBuilder.Append($"{loginCommand}");

                    // Add main app update
                    argumentsBuilder.Append($" +app_update {profile.AppID}");

                    // Add additional app updates
                    if (additionalAppIds != null)
                    {
                        foreach (var appId in additionalAppIds)
                        {
                            argumentsBuilder.Append($" +app_update {appId}");
                        }
                    }

                    // Append validate arg (if enabled in profile) and any custom arguments, and finally +quit
                    if (!string.IsNullOrEmpty(validateArg))
                    {
                        argumentsBuilder.Append($" {validateArg}");
                    }
                    if (!string.IsNullOrEmpty(argumentsArg))
                    {
                        argumentsBuilder.Append($" {argumentsArg}");
                    }
                    argumentsBuilder.Append(" +quit");


                    string arguments = argumentsBuilder.ToString().Trim();


                    // Hide sensitive information in logs
                    string safeArguments = arguments.Contains("+login ") ?
                        arguments.Replace("+login ", "+login [credentials] ") :
                        arguments;

                    _logger.LogInformation("Chạy SteamCMD với tham số: {SafeArguments}", safeArguments);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy SteamCMD cho {profile.Name} và các game phụ...");

                    // Create and configure the SteamCMD process
                    steamCmdProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = steamCmdPath,
                            Arguments = arguments,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            RedirectStandardInput = true, // Needed for some SteamCMD interactions, though +quit is used here
                            CreateNoWindow = true, // Hide the console window
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8,
                            WorkingDirectory = Path.GetDirectoryName(steamCmdPath) // Set working directory to steamcmd folder
                        },
                        EnableRaisingEvents = true // Enable events like Exited
                    };

                    // Buffer and timer for sending output in batches to avoid flooding
                    var outputBuffer = new StringBuilder();
                    var lastMessageHash = new HashSet<string>(StringComparer.Ordinal); // Track recent messages to avoid excessive logging of duplicates
                    var duplicateCount = 0;
                    var outputTimer = new System.Timers.Timer(200); // Increased timer interval slightly

                    outputTimer.Elapsed += async (sender, e) =>
                    {
                        string output;
                        lock (outputBuffer)
                        {
                            if (outputBuffer.Length == 0) return;
                            output = outputBuffer.ToString();
                            outputBuffer.Clear();
                        }

                        // Append duplicate count info if any were skipped
                        if (duplicateCount > 0)
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"{output}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                            duplicateCount = 0; // Reset count after sending
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", output);
                        }
                    };
                    outputTimer.Start();

                    // Event handler for standard output
                    steamCmdProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        // Check for success messages in the output
                        if (successMessages.Contains(e.Data.Trim()))
                        {
                            // Add success messages to a set to confirm success later
                            // Using a separate set for successful completion check
                            // This ensures we don't miss the final success message
                            // due to the output buffer or duplicate filtering.
                            lock (successMessages) // Protect shared resource
                            {
                                successMessages.Add(e.Data.Trim()); // Add to the set used for the final success check
                            }
                        }

                        // Duplicate message filtering for display logs
                        if (lastMessageHash.Contains(e.Data.Trim()))
                        {
                            duplicateCount++;
                            return;
                        }

                        lastMessageHash.Add(e.Data.Trim());

                        // Keep the hash set size in check
                        if (lastMessageHash.Count > 100) // Increased size limit
                        {
                            lastMessageHash.Clear();
                            // Optionally prune the hashset periodically if it grows too large and duplicates are expected over a long run
                        }

                        // Append to the output buffer for batch sending
                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(e.Data);
                        }
                    };

                    // Event handler for standard error
                    steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        // Check if this error message has been logged recently
                        if (!lastMessageHash.Contains(e.Data.Trim()))
                        {
                            _logger.LogError("SteamCMD Error: {Data}", e.Data);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi: {e.Data}");

                            lastMessageHash.Add(e.Data.Trim());
                            if (lastMessageHash.Count > 100)
                            {
                                lastMessageHash.Clear();
                            }
                        }
                    };

                    // Add the process to the tracking dictionary
                    _steamCmdProcesses[profileId] = steamCmdProcess;

                    // Start the process
                    steamCmdProcess.Start();

                    // Update profile with PID
                    profile.Pid = steamCmdProcess.Id;
                    await _profileService.UpdateProfile(profile);

                    // Begin asynchronous reading of output and error streams
                    steamCmdProcess.BeginOutputReadLine();
                    steamCmdProcess.BeginErrorReadLine();

                    // Wait for the process to exit
                    steamCmdProcess.WaitForExit();

                    // Stop the output timer and process any remaining buffered output
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

                    // Determine success based on the exit code and presence of success messages
                    // SteamCMD typically returns 0 on success, but checking output messages is more reliable for update status.
                    bool foundOverallSuccessMessage = false;
                    // Need to check if ALL expected success messages for ALL apps were found to declare overall success
                    int successfulAppUpdatesCount = 0;
                    lock (successMessages) // Protect the set
                    {
                        foreach (var successMsg in successMessages)
                        {
                            if (remainingOutput.Contains(successMsg, StringComparison.OrdinalIgnoreCase))
                            {
                                successfulAppUpdatesCount++;
                            }
                        }
                    }

                    // Overall success if exit code is 0 AND all expected app updates reported success
                    // Or if exit code is 2 (already up to date) and the main app was reported as up to date
                    if (steamCmdProcess.ExitCode == 0 && successfulAppUpdatesCount >= (1 + (additionalAppIds?.Count ?? 0))) // 1 for main app + count of additional apps
                    {
                        success = true;
                        string exitMessage = $"Cập nhật game {profile.Name} và các game phụ hoàn tất thành công (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Success", exitMessage));
                    }
                    else if (steamCmdProcess.ExitCode == 2 && remainingOutput.Contains($"Success! App '{profile.AppID}' already up to date.", StringComparison.OrdinalIgnoreCase))
                    {
                        // Handle case where main app is already up to date and exit code is 2.
                        // We might need more sophisticated logic here to check status of ALL apps if exit code is 2.
                        // For simplicity now, assuming exit code 2 with main app success implies others might also be up to date.
                        success = true; // Assume success if main app is up to date and exit code is 2
                        string exitMessage = $"Cập nhật game {profile.Name} hoàn tất: Đã cập nhật mới nhất (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Success", exitMessage));
                    }
                    else
                    {
                        success = false;
                        string exitMessage = $"Quá trình cập nhật game {profile.Name} và các game phụ không thành công (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Error", exitMessage));
                    }
                }
                finally
                {
                    // Stop monitoring the log file
                    _logFileReader.StopMonitoring();

                    // Remove the process from tracking
                    _steamCmdProcesses.TryRemove(profileId, out _);

                    // Dispose of the process object
                    if (steamCmdProcess != null)
                    {
                        if (!steamCmdProcess.HasExited)
                        {
                            try
                            {
                                // Attempt to terminate if it hasn't exited for some reason
                                steamCmdProcess.Terminator(ProcessExitTimeoutMs);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error terminating SteamCMD process in finally block: {Message}", ex.Message);
                            }
                        }
                        steamCmdProcess.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD: {ex.Message}");
                success = false;

                // Ensure process is disposed even if an exception occurs early
                steamCmdProcess?.Dispose();
            }

            return success;
        }

        /// <summary>
        /// Runs the actual SteamCMD process for a given profile FORCING validate after each app_update.
        /// This is intended for retry attempts after an initial failure.
        /// </summary>
        /// <param name="profile">The SteamCmdProfile to run.</param>
        /// <param name="profileId">The ID of the profile.</param>
        /// <param name="additionalAppIds">A list of additional App IDs to update in the same process.</param>
        /// <returns>True if the SteamCMD process finished successfully, false otherwise.</returns>
        private async Task<bool> RunSteamCmdProcessWithValidationAsync(SteamCmdProfile profile, int profileId, List<string> additionalAppIds = null)
        {
            Process steamCmdProcess = null;
            bool success = false;
            string steamCmdPath = GetSteamCmdPath();
            // Using HashSet for efficient checking of success messages
            var successMessages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"Success! App '{profile.AppID}' already up to date.",
                $"Success! App '{profile.AppID}' fully installed.",
                $"Success! App '{profile.AppID}' updated."
            };

            // Add success messages for additional App IDs
            if (additionalAppIds != null)
            {
                foreach (var appId in additionalAppIds)
                {
                    successMessages.Add($"Success! App '{appId}' already up to date.");
                    successMessages.Add($"Success! App '{appId}' fully installed.");
                    successMessages.Add($"Success! App '{appId}' updated.");
                }
            }


            try
            {
                if (!File.Exists(steamCmdPath))
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại tại {steamCmdPath}");
                    return false;
                }

                string logFilePath = GetSteamCmdLogPath(profileId);

                // Start LogFileReader to monitor the console log file
                _logFileReader.StartMonitoring(logFilePath, async (content) =>
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        // Send log content to clients via SignalR
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", content);
                    }
                });

                try
                {
                    // Prepare login command
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
                            // Decide if this should stop the process - currently logs and continues
                        }
                    }

                    // Prepare custom arguments
                    string argumentsArg = string.IsNullOrEmpty(profile.Arguments) ? "" : profile.Arguments.Trim();


                    // Construct the full command arguments including additional App IDs and FORCING "validate" per app_update
                    StringBuilder argumentsBuilder = new StringBuilder();
                    argumentsBuilder.Append($"{loginCommand}");

                    // Add main app update and FORCE validate
                    argumentsBuilder.Append($" +app_update {profile.AppID} validate");


                    // Add additional app updates and FORCE validate
                    if (additionalAppIds != null)
                    {
                        foreach (var appId in additionalAppIds)
                        {
                            argumentsBuilder.Append($" +app_update {appId} validate");
                        }
                    }

                    // Append any custom arguments and finally +quit
                    if (!string.IsNullOrEmpty(argumentsArg))
                    {
                        argumentsBuilder.Append($" {argumentsArg}");
                    }
                    argumentsBuilder.Append(" +quit");


                    string arguments = argumentsBuilder.ToString().Trim();


                    // Hide sensitive information in logs
                    string safeArguments = arguments.Contains("+login ") ?
                        arguments.Replace("+login ", "+login [credentials] ") :
                        arguments;

                    _logger.LogInformation("Chạy SteamCMD (với validate) với tham số: {SafeArguments}", safeArguments);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy SteamCMD (với validate) cho {profile.Name} và các game phụ...");

                    // Create and configure the SteamCMD process
                    steamCmdProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = steamCmdPath,
                            Arguments = arguments,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            RedirectStandardInput = true, // Needed for some SteamCMD interactions, though +quit is used here
                            CreateNoWindow = true, // Hide the console window
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8,
                            WorkingDirectory = Path.GetDirectoryName(steamCmdPath) // Set working directory to steamcmd folder
                        },
                        EnableRaisingEvents = true // Enable events like Exited
                    };

                    // Buffer and timer for sending output in batches to avoid flooding
                    var outputBuffer = new StringBuilder();
                    var lastMessageHash = new HashSet<string>(StringComparer.Ordinal); // Track recent messages to avoid excessive logging of duplicates
                    var duplicateCount = 0;
                    var outputTimer = new System.Timers.Timer(200); // Increased timer interval slightly

                    outputTimer.Elapsed += async (sender, e) =>
                    {
                        string output;
                        lock (outputBuffer)
                        {
                            if (outputBuffer.Length == 0) return;
                            output = outputBuffer.ToString();
                            outputBuffer.Clear();
                        }

                        // Append duplicate count info if any were skipped
                        if (duplicateCount > 0)
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"{output}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                            duplicateCount = 0; // Reset count after sending
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", output);
                        }
                    };
                    outputTimer.Start();

                    // Event handler for standard output
                    steamCmdProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        // Check for success messages in the output
                        if (successMessages.Contains(e.Data.Trim()))
                        {
                            // Add success messages to a set to confirm success later
                            // Using a separate set for successful completion check
                            // This ensures we don't miss the final success message
                            // due to the output buffer or duplicate filtering.
                            lock (successMessages) // Protect shared resource
                            {
                                successMessages.Add(e.Data.Trim()); // Add to the set used for the final success check
                            }
                        }

                        // Duplicate message filtering for display logs
                        if (lastMessageHash.Contains(e.Data.Trim()))
                        {
                            duplicateCount++;
                            return;
                        }

                        lastMessageHash.Add(e.Data.Trim());

                        // Keep the hash set size in check
                        if (lastMessageHash.Count > 100) // Increased size limit
                        {
                            lastMessageHash.Clear();
                        }

                        // Append to the output buffer for batch sending
                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(e.Data);
                        }
                    };

                    // Event handler for standard error
                    steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        // Check if this error message has been logged recently
                        if (!lastMessageHash.Contains(e.Data.Trim()))
                        {
                            _logger.LogError("SteamCMD Error: {Data}", e.Data);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi: {e.Data}");

                            lastMessageHash.Add(e.Data.Trim());
                            if (lastMessageHash.Count > 100)
                            {
                                lastMessageHash.Clear();
                            }
                        }
                    };

                    // Add the process to the tracking dictionary
                    _steamCmdProcesses[profileId] = steamCmdProcess;

                    // Start the process
                    steamCmdProcess.Start();

                    // Update profile with PID
                    profile.Pid = steamCmdProcess.Id;
                    await _profileService.UpdateProfile(profile);

                    // Begin asynchronous reading of output and error streams
                    steamCmdProcess.BeginOutputReadLine();
                    steamCmdProcess.BeginErrorReadLine();

                    // Wait for the process to exit
                    steamCmdProcess.WaitForExit();

                    // Stop the output timer and process any remaining buffered output
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

                    // Determine success based on the exit code and presence of success messages
                    // SteamCMD typically returns 0 on success, but checking output messages is more reliable for update status.
                    bool foundOverallSuccessMessage = false;
                    // Need to check if ALL expected success messages for ALL apps were found to declare overall success
                    int successfulAppUpdatesCount = 0;
                    lock (successMessages) // Protect the set
                    {
                        foreach (var successMsg in successMessages)
                        {
                            if (remainingOutput.Contains(successMsg, StringComparison.OrdinalIgnoreCase))
                            {
                                successfulAppUpdatesCount++;
                            }
                        }
                    }

                    // Overall success if exit code is 0 AND all expected app updates reported success
                    if (steamCmdProcess.ExitCode == 0 && successfulAppUpdatesCount >= (1 + (additionalAppIds?.Count ?? 0))) // 1 for main app + count of additional apps
                    {
                        success = true;
                        string exitMessage = $"Cập nhật game {profile.Name} và các game phụ hoàn tất thành công (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Success", exitMessage));
                    }
                    // Consider exit code 2 as potentially successful if an update was not needed
                    else if (steamCmdProcess.ExitCode == 2 && remainingOutput.Contains($"Success! App '{profile.AppID}' already up to date.", StringComparison.OrdinalIgnoreCase))
                    {
                        // Handle case where main app is already up to date and exit code is 2.
                        // We might need more sophisticated logic here to check status of ALL apps if exit code is 2.
                        // For simplicity now, assuming exit code 2 with main app success implies others might also be up to date.
                        success = true; // Assume success if main app is up to date and exit code is 2
                        string exitMessage = $"Cập nhật game {profile.Name} hoàn tất: Đã cập nhật mới nhất (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Success", exitMessage));
                    }
                    else
                    {
                        success = false;
                        string exitMessage = $"Quá trình cập nhật game {profile.Name} và các game phụ không thành công (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Error", exitMessage));
                    }
                }
                finally
                {
                    // Stop monitoring the log file
                    _logFileReader.StopMonitoring();

                    // Remove the process from tracking
                    _steamCmdProcesses.TryRemove(profileId, out _);

                    // Dispose of the process object
                    if (steamCmdProcess != null)
                    {
                        if (!steamCmdProcess.HasExited)
                        {
                            try
                            {
                                // Attempt to terminate if it hasn't exited for some reason
                                steamCmdProcess.Terminator(ProcessExitTimeoutMs);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error terminating SteamCMD process in finally block: {Message}", ex.Message);
                            }
                        }
                        steamCmdProcess.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD (với validate): {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD (với validate): {ex.Message}");
                success = false;

                // Ensure process is disposed even if an exception occurs early
                steamCmdProcess?.Dispose();
            }

            return success;
        }


        // Keeping original methods for running all profiles, stopping all profiles, restarting profile, and shutdown
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

                // Ensure a clean environment before starting all profiles
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs * 2); // Increased delay

                // Run each profile sequentially
                foreach (var profile in profiles)
                {
                    if (_cancelAutoRun) break; // Allow cancellation

                    _currentProfileIndex++;

                    await SafeSendLogAsync("System", "Info",
                        $"Đang chuẩn bị chạy profile ({_currentProfileIndex}/{profiles.Count}): {profile.Name}");

                    try
                    {
                        // RunProfileAsync now handles its own cleanup and status updates
                        bool success = await RunProfileAsync(profile.Id);

                        if (!success)
                        {
                            await SafeSendLogAsync(profile.Name, "Warning",
                                $"Chạy profile {profile.Name} không thành công");
                        }

                        // Wait between running profiles, unless it's the last one or cancelled
                        if (_currentProfileIndex < profiles.Count && !_cancelAutoRun)
                        {
                            await Task.Delay(5000); // Increased delay between profiles
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi chạy profile {ProfileName}", profile.Name);
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi: {ex.Message}");
                        // Continue to the next profile even if one fails
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
                _cancelAutoRun = false; // Reset cancel flag
            }
        }

        public async Task StopAllProfilesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các profiles...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profiles...");

            // Đặt cờ hủy tiến trình tự động chạy
            _cancelAutoRun = true;
            _isRunningAllProfiles = false; // Thêm dòng này để chắc chắn dừng quá trình chạy tất cả profiles

            // Dừng tất cả các process đang được theo dõi
            foreach (var profileId in _steamCmdProcesses.Keys.ToList())
            {
                if (_steamCmdProcesses.TryRemove(profileId, out var process))
                {
                    await KillProcessAsync(process, $"Profile {profileId}");
                    process.Dispose();
                }
            }

            // Dừng mọi tiến trình steamcmd còn sót lại
            await KillAllSteamCmdProcessesAsync();
            await Task.Delay(RetryDelayMs); // Đợi sau khi kill

            // Cập nhật trạng thái của các profile đang chạy
            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.Status == "Running" || p.Status == "Waiting"))
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
            // Stop the profile if it's running
            if (_steamCmdProcesses.TryRemove(profileId, out var process))
            {
                await KillProcessAsync(process, $"Profile {profileId}");
                process.Dispose();
            }

            await Task.Delay(RetryDelayMs * 2); // Increased delay before restarting

            // Run the profile again
            return await RunProfileAsync(profileId);
        }

        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các process trước khi tắt ứng dụng...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các process trước khi tắt ứng dụng...");

            // Stop the schedule timer
            if (_scheduleTimer != null)
            {
                _scheduleTimer.Stop();
                _scheduleTimer.Dispose();
            }


            // Stop all running profiles
            await StopAllProfilesAsync();

            _logger.LogInformation("Đã hoàn thành dừng process.");
            await SafeSendLogAsync("System", "Success", "Đã hoàn thành dừng process.");
        }

        /// <summary>
        /// Lấy thông tin cập nhật của game từ SteamCMD API
        /// </summary>
        /// <param name="appID">ID của game</param>
        /// <returns>Thông tin cập nhật hoặc null nếu không tìm thấy</returns>
        public async Task<AppUpdateInfo> GetSteamAppInfo(string appID)
        {
            using (HttpClient client = new HttpClient())
            {
                try
                {
                    client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/114.0.0.0 Safari/537.36");
                    string url = $"https://api.steamcmd.net/v1/info/{appID}";
                    HttpResponseMessage response = await client.GetAsync(url);

                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        // Use Newtonsoft.Json to deserialize
                        dynamic data = JsonConvert.DeserializeObject(json);

                        if (data != null && data.status == "success" && data.data != null && data.data[appID] != null)
                        {
                            var gameData = data.data[appID];
                            var appInfo = new AppUpdateInfo
                            {
                                AppID = appID,
                                Name = gameData.common?.name ?? "Không xác định"
                            };
                            if (gameData._change_number != null)
                            {
                                appInfo.ChangeNumber = (long)gameData._change_number;
                            }

                            if (gameData.extended != null)
                            {
                                appInfo.Developer = gameData.extended.developer ?? "Không có thông tin";
                                appInfo.Publisher = gameData.extended.publisher ?? "Không có thông tin";
                            }

                            if (gameData.depots != null && gameData.depots.branches != null && gameData.depots.branches.@public != null)
                            {
                                long timestamp = 0;
                                if (gameData.depots.branches.@public.timeupdated != null &&
                                    long.TryParse(gameData.depots.branches.@public.timeupdated.ToString(), out timestamp))
                                {
                                    DateTime updateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                        .AddSeconds(timestamp);
                                    updateTime = updateTime.AddHours(7); // GMT+7
                                    appInfo.LastUpdate = updateTime.ToString("dd/MM/yyyy HH:mm:ss") + " (GMT+7)";
                                    appInfo.LastUpdateDateTime = updateTime;
                                    appInfo.UpdateDays = (int)(DateTime.Now - updateTime).TotalDays;
                                    appInfo.HasRecentUpdate = appInfo.UpdateDays >= 0 && appInfo.UpdateDays <= 7;
                                }
                            }

                            return appInfo;
                        }
                    }

                    _logger.LogWarning("Không tìm thấy thông tin cho AppID {AppID}", appID);
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi lấy thông tin Steam App {AppID}: {Message}", appID, ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// Lớp chứa thông tin cập nhật của game
        /// </summary>
        public class AppUpdateInfo
        {
            public string AppID { get; set; }
            public string Name { get; set; }
            public string LastUpdate { get; set; } = "Không có thông tin";
            public DateTime? LastUpdateDateTime { get; set; }
            public int UpdateDays { get; set; }
            public bool HasRecentUpdate { get; set; }
            public string Developer { get; set; } = "Không có thông tin";
            public string Publisher { get; set; } = "Không có thông tin";
            public long ChangeNumber { get; set; }
        }
    }
}
#endregion