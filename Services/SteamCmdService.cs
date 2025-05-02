using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
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
    public class SteamCmdService : IDisposable
    {
        private readonly ILogger<SteamCmdService> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly ProfileService _profileService;
        private readonly SettingsService _settingsService;
        private readonly EncryptionService _encryptionService;
        private readonly LogFileReader _logFileReader;
        private readonly SteamApiService _steamApiService;
        private readonly DependencyManagerService _dependencyManagerService;
        private readonly LogService _logService;
        private readonly LicenseService _licenseService;
        private readonly IServiceProvider _serviceProvider;


        private const int MaxLogEntries = 5000;
        private const int RetryDelayMs = 15000;  // Tăng từ 5000 lên 15000
        private const int ProcessExitTimeoutMs = 60000;  // Tăng từ 20000 lên 60000

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

        private readonly HashSet<string> appIdsToRetry = new HashSet<string>();

        private readonly ConcurrentQueue<(string status, string profileName, string message)> _logBuffer = new ConcurrentQueue<(string, string, string)>();
        private readonly System.Timers.Timer _logProcessTimer;
        private const int LOG_BATCH_SIZE = 10;
        private const int LOG_PROCESS_INTERVAL = 500; // 500ms

        private bool _disposed = false;

        private readonly string _logFilePath;
        private static readonly object _logFileLock = new object();

        // Thêm các Regex patterns được compiled sẵn để tối ưu performance
        private static readonly Regex[] SkipPatterns = new[]
        {
            new Regex(@"^(Un)?[Ll]oading Steam API\.\.\.OK$", RegexOptions.Compiled),
            new Regex(@"^(Redirecting stderr to |Logging directory: )'.*?'$", RegexOptions.Compiled),
            new Regex(@"^\[\s*0%\] Checking for available updates\.\.\.$", RegexOptions.Compiled),
            new Regex(@"^\[----\].*Verifying installation\.\.\.$", RegexOptions.Compiled),
            new Regex(@"^Waiting for (client config|user info)\.\.\.OK$", RegexOptions.Compiled),
            new Regex(@"^Steam Console Client \(c\) Valve Corporation - version \d+$", RegexOptions.Compiled),
            new Regex(@"^-- type 'quit' to exit --$", RegexOptions.Compiled),
            new Regex(@"^Logging in using username/password\.$", RegexOptions.Compiled),
            new Regex(@"^Logging in user '.*?' \[U:\d+:\d+\] to Steam Public\.\.\..*$", RegexOptions.Compiled)
        };

        // Regex patterns cho việc lọc thông tin nhạy cảm
        private static readonly Regex[] SensitiveInfoPatterns = new[]
        {
            new Regex(@"Logging in user '(.*?)' \[U:1:\d+\]", RegexOptions.Compiled),
            new Regex(@"Redirecting stderr to '(.+?)steamcmd\\logs\\stderr\.txt'", RegexOptions.Compiled),
            new Regex(@"Logging directory: '(.+?)steamcmd/logs'", RegexOptions.Compiled),
            new Regex(@"\+login\s+\S+\s+\S+", RegexOptions.Compiled)
        };

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
            DependencyManagerService dependencyManagerService,
            LogService logService,
            LicenseService licenseService,
            IServiceProvider serviceProvider)
            
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _settingsService = settingsService;
            _encryptionService = encryptionService;
            _logFileReader = logFileReader;
            _steamApiService = steamApiService;
            _dependencyManagerService = dependencyManagerService;
            _logService = logService;
            _licenseService = licenseService;
            _serviceProvider = serviceProvider;

            _logFilePath = Path.Combine(Directory.GetCurrentDirectory(), "Logs", "steamcmd.log");
            var logDir = Path.GetDirectoryName(_logFilePath);
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            
            // Khởi tạo timer
            _scheduleTimer = new System.Timers.Timer(60000); // 1 phút
            _scheduleTimer.Elapsed += async (sender, e) => await CheckScheduleAsync();
            _scheduleTimer.AutoReset = true;
            _scheduleTimer.Start();

            _hubMessageCleanupTimer = new System.Timers.Timer(HubMessageCleanupIntervalMs);
            _hubMessageCleanupTimer.Elapsed += (sender, e) => CleanupRecentHubMessages();
            _hubMessageCleanupTimer.AutoReset = true;
            _hubMessageCleanupTimer.Start();

            _logProcessTimer = new System.Timers.Timer(LOG_PROCESS_INTERVAL);
            _logProcessTimer.Elapsed += async (sender, e) => await ProcessLogBufferAsync();
            _logProcessTimer.AutoReset = true;
            _logProcessTimer.Start();

            // Khởi động LogFileReader với đường dẫn file log và callback xử lý
            _logFileReader.StartMonitoring(_logFilePath, (newLogContent) => {
                _logger.LogDebug("Nhận được log mới: {Length} ký tự", newLogContent?.Length ?? 0);
            });

            LoadExistingLogs();
            
            // Kiểm tra license khi khởi động
            Task.Run(async () => {
                try 
                {
                    bool licenseValid = await _licenseService.CheckLicenseBeforeOperationAsync();
                    if (licenseValid)
                    {
                        await SafeSendLogAsync("System", "Info", "Khởi động dịch vụ thành công - License hợp lệ");
                        _logger.LogInformation("Khởi động dịch vụ thành công - License hợp lệ");
                        
                        // Tự động cài đặt SteamCMD nếu cần
                        if (!await IsSteamCmdInstalled())
                        {
                            await SafeSendLogAsync("System", "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                            await InstallSteamCmd();
                        }
                    }
                    else
                    {
                        await SafeSendLogAsync("System", "Error", "Dịch vụ không thể hoạt động đầy đủ - License không hợp lệ");
                        _logger.LogWarning("Dịch vụ không thể hoạt động đầy đủ - License không hợp lệ");
                        
                        // Thông báo cho người dùng qua Hub
                        try 
                        {
                            await _hubContext.Clients.All.SendAsync("LicenseError", "License không hợp lệ. Vui lòng cập nhật License để sử dụng dịch vụ.");
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra license lúc khởi động: {Error}", ex.Message);
                }
            });
        }

        private void LoadExistingLogs()
        {
            try
            {
                if (File.Exists(_logFilePath))
                {
                    lock (_logs)
                    {
                        var fileLines = File.ReadAllLines(_logFilePath);
                        foreach (var line in fileLines)
                        {
                            try
                            {
                                var parts = line.Split('|');
                                if (parts.Length >= 4)
                                {
                                    var timestamp = DateTime.Parse(parts[0]);
                                    var profileName = parts[1];
                                    var status = parts[2];
                                    var message = parts[3];

                                    _logs.Add(new LogEntry(timestamp, profileName, status, message));
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Lỗi khi đọc dòng log: {Line}", line);
                            }
                        }

                        // Giới hạn số lượng log
                        if (_logs.Count > MaxLogEntries)
                        {
                            _logs.RemoveRange(0, _logs.Count - MaxLogEntries);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc file log");
            }
        }

        private void SaveLogToFile(LogEntry entry)
        {
            try
            {
                string logLine = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss}|{entry.ProfileName}|{entry.Status}|{entry.Message}";
                lock (_logFileLock)
                {
                    File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi ghi log vào file");
            }
        }

        private void AddLog(LogEntry entry)
        {
            // Chỉ lưu các log quan trọng
            if (IsImportantLog(entry))
            {
                lock (_logs)
                {
                    _logs.Add(entry);
                    if (_logs.Count > MaxLogEntries)
                    {
                        _logs.RemoveRange(0, _logs.Count - MaxLogEntries);
                    }
                }

                // Lưu log vào file
                SaveLogToFile(entry);

                // Thêm log vào LogService với màu xanh lá cây cho log thành công
                string level = entry.Status.Equals("Success", StringComparison.OrdinalIgnoreCase) ? "SUCCESS" : "INFO";
                _logService.AddLog(level, entry.Message, entry.ProfileName, entry.Status);
            }
        }

        public void ClearLogs()
        {
            try
            {
                lock (_logs)
                {
                    _logs.Clear();
                }

                // Xóa file log
                lock (_logFileLock)
                {
                    if (File.Exists(_logFilePath))
                    {
                        File.Delete(_logFilePath);
                    }
                }

                _logger.LogInformation("Đã xóa toàn bộ log");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa log");
            }
        }

        #region Log and Notification Methods
        private bool IsImportantLog(LogEntry entry)
        {
            // Lưu lại các thông báo cụ thể về đăng nhập và cập nhật game
            return (entry.Message.Contains("Success! App", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Error! App", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("cập nhật thành công", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("cập nhật thất bại", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Cập nhật", StringComparison.OrdinalIgnoreCase) && 
                   (entry.Message.Contains("không thành công", StringComparison.OrdinalIgnoreCase) || 
                    entry.Message.Contains("thành công", StringComparison.OrdinalIgnoreCase)) ||
                   entry.Message.Contains("Đăng nhập Steam thành công", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Thử lại cho", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("hoàn tất thành công", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Cập nhật thành công cho", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Đã xử lý cập nhật thành công cho", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Cập nhật thành công cho", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi đăng nhập", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi kết nối Steam", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi thời gian thực", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi nghiêm trọng", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi chạy SteamCMD", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi cập nhật", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi đăng nhập", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi giải mã", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi đọc file", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi tạo thư mục", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi tạo liên kết", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi xóa file", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi xóa thư mục", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi tải file", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi giải nén", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi cài đặt", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi khởi động", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi dừng", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi khởi động lại", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi tắt", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi kiểm tra", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi xử lý", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi gửi log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi nhận log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi lưu log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi đọc log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi xóa log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi dọn dẹp log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi kiểm tra log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi xử lý log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi gửi log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi nhận log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi lưu log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi đọc log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi xóa log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi dọn dẹp log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi kiểm tra log", StringComparison.OrdinalIgnoreCase) ||
                   entry.Message.Contains("Lỗi khi xử lý log", StringComparison.OrdinalIgnoreCase));
        }

        private bool ShouldSkipLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return true;

            string testLine = line.Trim();
            return SkipPatterns.Any(regex => regex.IsMatch(testLine));
        }

        private string SanitizeLogMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;

            string sanitizedMessage = message;

            // Lọc các log không cần thiết từ SteamCMD
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^Unloading Steam API\.\.\.OK$", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^Loading Steam API\.\.\.OK$", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"(Redirecting stderr to |Logging directory: )'(.*?)'", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^\[\s*0%\] Checking for available updates\.\.\.$", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^\[----\].*Verifying installation\.\.\.$", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^Waiting for (client config|user info)\.\.\.OK$", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^Steam Console Client \(c\) Valve Corporation - version \d+$", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^-- type 'quit' to exit --$", "", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^Logging in using username/password\.$", "", RegexOptions.IgnoreCase);
            
            // Lọc thông tin nhạy cảm
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"Logging in user '(.*?)' \[U:1:\d+\]", "Logging in user '***' [U:1:***]", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"\+login\s+\S+\s+\S+", "+login [credentials]", RegexOptions.IgnoreCase);

            // Lọc các đường dẫn
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"'[A-Za-z]:\\.*?\\steamcmd\\.*?'", "'***'", RegexOptions.IgnoreCase);
            sanitizedMessage = Regex.Replace(sanitizedMessage, @".*?/steamcmd/.*?'", "'***'", RegexOptions.IgnoreCase);

            // Loại bỏ các dòng trống sau khi thay thế
            sanitizedMessage = Regex.Replace(sanitizedMessage, @"^\s*$\n|\r", "", RegexOptions.Multiline);

            return sanitizedMessage.Trim();
        }

        private async Task SafeSendLogAsync(string profileName, string status, string message)
        {
            try
            {
                _logBuffer.Enqueue((status, profileName, message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm log vào buffer: {Message}", ex.Message);
            }
        }

        public List<LogEntry> GetLogs()
        {
            lock (_logs)
            {
                return _logs.OrderByDescending(l => l.Timestamp).ToList();
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
            if (process == null) return true;

            try
            {
                if (!process.HasExited)
                {
                    _logger.LogInformation("Đang dừng process cho profile {ProfileName}", profileName);
                    process.Kill();
                    await Task.Delay(1000); // Chờ 1 giây

                    // Chờ process thoát với timeout
                    var exitTask = Task.Run(() => process.WaitForExit(ProcessExitTimeoutMs));
                    if (await Task.WhenAny(exitTask, Task.Delay(ProcessExitTimeoutMs)) == exitTask)
                    {
                        _logger.LogInformation("Process đã dừng thành công cho profile {ProfileName}", profileName);
                return true;
                    }
                    else
                    {
                        _logger.LogWarning("Quá thời gian chờ process dừng cho profile {ProfileName}", profileName);
                        try
                        {
                            process.Kill(true); // Force kill
                        }
                        catch { }
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng process cho profile {ProfileName}", profileName);
                return false;
            }
        }

        public async Task<bool> KillAllSteamCmdProcessesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các tiến trình SteamCMD...");
            bool success = true;
            try
            {
                // Dừng các tiến trình đã được theo dõi
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    if (!await KillProcessAsync(kvp.Value, $"Profile {kvp.Key}"))
                    {
                        success = false;
                    }
                    _steamCmdProcesses.TryRemove(kvp.Key, out _);
                }

                // Tìm kiếm mọi tiến trình steamcmd còn lại trong hệ thống
                try
                {
                    var steamCmdProcesses = Process.GetProcessesByName("steamcmd");
                    if (steamCmdProcesses.Length > 0)
                    {
                        _logger.LogInformation("Tìm thấy {Count} tiến trình SteamCMD bổ sung cần dừng", steamCmdProcesses.Length);
                        foreach (var process in steamCmdProcesses)
                        {
                            try
                            {
                                await KillProcessAsync(process, "Hệ thống");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Không thể dừng tiến trình SteamCMD (PID: {Pid}): {Message}", process.Id, ex.Message);
                                success = false;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Lỗi khi tìm kiếm tiến trình SteamCMD: {Message}", ex.Message);
                    success = false;
                }

                // Tìm kiếm các tiến trình SteamService
                try
                {
                    var steamServiceProcesses = Process.GetProcessesByName("steamservice");
                    if (steamServiceProcesses.Length > 0)
                    {
                        _logger.LogInformation("Tìm thấy {Count} tiến trình SteamService cần dừng", steamServiceProcesses.Length);
                        foreach (var process in steamServiceProcesses)
                        {
                            try
                            {
                                process.Kill(true);
                                _logger.LogInformation("Đã dừng tiến trình SteamService (PID: {Pid})", process.Id);
                }
                catch (Exception ex)
                {
                                _logger.LogWarning("Không thể dừng tiến trình SteamService (PID: {Pid}): {Message}", process.Id, ex.Message);
                            }
                        }
                    }
                }
                catch { }
                
                // Sử dụng taskkill để đảm bảo mọi tiến trình đã dừng
                _logger.LogInformation("Sử dụng taskkill để đảm bảo tất cả steamcmd.exe đã dừng...");
                CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe /T", ProcessExitTimeoutMs);
                
                    await Task.Delay(2000);
                    
                // Kiểm tra lại xem còn tiến trình nào không
                var remainingProcesses = Process.GetProcessesByName("steamcmd");
                if (remainingProcesses.Length > 0)
                {
                    _logger.LogWarning("Vẫn còn {Count} tiến trình SteamCMD sau khi dừng, thử lại lần cuối...", 
                        remainingProcesses.Length);
                    CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe /T", ProcessExitTimeoutMs);
                    await Task.Delay(3000);  // Tăng thời gian đợi
                    
                    // Kiểm tra lại lần nữa
                    remainingProcesses = Process.GetProcessesByName("steamcmd");
                    if (remainingProcesses.Length > 0)
                    {
                        _logger.LogError("Không thể dừng {Count} tiến trình SteamCMD sau nhiều lần thử", 
                            remainingProcesses.Length);
                        success = false;
                    }
                }
                }
                catch (Exception ex)
                {
                _logger.LogError(ex, "Lỗi không mong muốn trong KillAllSteamCmdProcessesAsync: {Message}", ex.Message);
                success = false;
                }

            if (success)
            {
                _logger.LogInformation("Đã dừng tất cả các tiến trình SteamCMD thành công");
            }
            else
            {
                _logger.LogWarning("Một số tiến trình SteamCMD có thể vẫn đang chạy");
            }

            return success;
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
        // File: Services/SteamCmdService.cs
        // Thay thế phương thức PrepareFolderStructure
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

            // Tạo thư mục cha nếu chưa tồn tại
                    Directory.CreateDirectory(Path.GetDirectoryName(localSteamappsLinkDir));

            // Chỉ dùng symbolic link
            try
            {
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
        #endregion

        #region Public API Methods (Queueing and Execution)

        public async Task<bool> QueueProfileForUpdate(int profileId)
        {
            if (await IsAlreadyInQueueAsync(profileId, null))
            {
                _logger.LogWarning("Profile {ProfileId} đã trong queue, bỏ qua yêu cầu", profileId);
                return false;
            }

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                _logger.LogError("Không tìm thấy profile {ProfileId}", profileId);
                    return false;
                }

            // Kiểm tra xem có process nào đang chạy cho profile này không
            if (_steamCmdProcesses.TryGetValue(profileId, out var existingProcess))
            {
                if (!existingProcess.HasExited)
                {
                    _logger.LogWarning("Profile {ProfileId} đang chạy, không thể thêm vào queue", profileId);
                    return false;
                }
                else
                {
                    _steamCmdProcesses.TryRemove(profileId, out _);
                }
            }

                _updateQueue.Enqueue(profileId);
            _logger.LogInformation("Đã thêm profile {ProfileId} vào queue", profileId);
                StartQueueProcessorIfNotRunning();
                return true;
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
                _logger.LogWarning("ProcessUpdateQueueAsync đã đang chạy. Bỏ qua.");
                return;
            }
            
            _isProcessingQueue = true;
            
            try
            {
                _logger.LogInformation("Bắt đầu xử lý hàng đợi (Số lượng: {0})", _updateQueue.Count);
                await SafeSendLogAsync("System", "Info", $"Bắt đầu xử lý hàng đợi cập nhật (Số lượng: {_updateQueue.Count})");

                // Xử lý tất cả các mục trong hàng đợi
                while (_updateQueue.TryDequeue(out int profileId))
                {
                    var profile = await _profileService.GetProfileById(profileId);
                    if (profile == null) continue;

                    _lastRunHadLoginError = false;

                    try
                    {
                        _logger.LogInformation("Đang xử lý cập nhật cho profile '{0}'...", profile.Name);
                        await SafeSendLogAsync(profile.Name, "Info", $"Đang xử lý cập nhật...");

                        // Đảm bảo tiến trình SteamCMD hoàn toàn bị dừng trước khi bắt đầu cập nhật mới
                        await KillAllSteamCmdProcessesAsync();
                        await Task.Delay(5000); // Tăng thời gian chờ lên 5 giây

                        // Dọn dẹp các file tạm thời có thể gây xung đột
                        await CleanupSteamCmdTemporaryFiles();

                        bool success = await ExecuteProfileUpdateAsync(profileId, null);

                        if (success)
                        {
                            _logger.LogInformation("Cập nhật thành công cho profile '{0}'", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Success", $"Cập nhật thành công.");
                        }
                        else
                        {
                            _logger.LogWarning("Cập nhật không thành công cho profile '{0}'", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Cập nhật không thành công. Kiểm tra log.");
                        }

                        // Tăng thời gian đợi giữa các profile để tránh xung đột
                        await Task.Delay(10000); // Tăng lên 10 giây
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý cập nhật cho profile '{0}'", profile?.Name ?? $"ID {profileId}");
                        if (profile != null)
                        {
                            profile.Status = "Error";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình xử lý hàng đợi");
            }
            finally
            {
                _isProcessingQueue = false;
                if (_isRunningAllProfiles)
                {
                    _isRunningAllProfiles = false;
                }
            }
        }

        public async Task<bool> RunProfileAsync(int id)
        {
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                await SafeSendLogAsync("System", "Error", "Không thể thực hiện thao tác - License không hợp lệ");
                
                // Thông báo cho người dùng qua Hub
                try 
                {
                    await _hubContext.Clients.All.SendAsync("LicenseError", "License không hợp lệ. Vui lòng cập nhật License để tiếp tục sử dụng dịch vụ.");
                }
                catch { }
                
                return false;
            }

            // Khi chạy một profile riêng lẻ từ UI, chỉ chạy app ID chính
            var profile = await _profileService.GetProfileById(id);
            if (profile == null)
            {
                _logger.LogWarning("RunProfileAsync: Không tìm thấy profile ID {ProfileId} để chạy", id);
                await SafeSendLogAsync("System", "Error", $"Không tìm thấy profile ID {id} để chạy");
                return false;
            }

            _logger.LogInformation("RunProfileAsync: Chuẩn bị chạy profile '{ProfileName}' (ID: {ProfileId}) - chỉ AppID chính",
                profile.Name, id);

            // Thông báo bắt đầu cập nhật
            string gameName = await GetGameNameFromAppId(profile.AppID);
            await SafeSendLogAsync(profile.Name, "Info", $"Bắt đầu cập nhật game {gameName} (AppID: {profile.AppID})");

            // Đảm bảo _isRunningAllProfiles = false khi chạy riêng profile
            // Điều này sẽ đảm bảo chỉ app chính được chạy
            _isRunningAllProfiles = false;

            // Thêm profile vào hàng đợi để xử lý (ExecuteProfileUpdateAsync sẽ chạy app chính)
            return await QueueProfileForUpdate(id);
        }

        // Updated method signature to support specificAppId based on 1.txt point 1
        public async Task<bool> ExecuteProfileUpdateAsync(int id, string specificAppId = null, bool forceValidate = false)
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

            // Đảm bảo tiến trình SteamCMD bị dừng hoàn toàn và dọn dẹp tất cả file tạm trước khi bắt đầu
            await KillAllSteamCmdProcessesAsync();
            await Task.Delay(3000);
            await CleanupSteamCmdTemporaryFiles();
            await Task.Delay(2000);
            
            // Thực hiện thêm bước dọn dẹp hệ thống khi chạy profile thứ 2 trở đi
            try {
                // Xác nhận không còn tiến trình nào
                if (Process.GetProcessesByName("steamcmd").Length > 0)
                {
                    _logger.LogWarning("Vẫn còn tiến trình SteamCMD sau khi cố gắng dừng, thực hiện dọn dẹp triệt để");
                    CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe /T", 10000);
                    await Task.Delay(3000);
                }
            } catch { }

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

            // Thêm kiểm tra đặc biệt cho trường hợp steamcmd báo thành công giả
            if (initialRunResult.ExitCode == -200 || initialRunResult.ExitCode == -201)
            {
                _logger.LogError("ExecuteProfileUpdateAsync: SteamCMD không thực sự chạy hoặc cập nhật, mặc dù báo thành công");
                await SafeSendLogAsync(profile.Name, "Error", "SteamCMD không thực sự chạy hoặc cập nhật, kiểm tra lại cài đặt hoặc thư mục cài đặt");
                
                // Thử chạy lại với cờ validate để ép buộc kiểm tra
                await SafeSendLogAsync(profile.Name, "Info", "Đang thử lại với tùy chọn validate...");
                
                initialRunResult = await RunSteamCmdProcessAsync(profile, id, appIdsToUpdate, forceValidate: true);
                if (initialRunResult.ExitCode < 0) // Vẫn gặp lỗi
                {
                    profile.Status = "Error";
                    profile.StopTime = DateTime.Now;
                    await _profileService.UpdateProfile(profile);
                    return false;
                }
            }

            // Kiểm tra lỗi đăng nhập
            if (_lastRunHadLoginError)
            {
                _logger.LogError("ExecuteProfileUpdateAsync: Phát hiện lỗi đăng nhập sau khi RunSteamCmdProcessAsync hoàn tất");
                profile.Status = "Error";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);
                await SafeSendLogAsync(profile.Name, "Error", "Lỗi đăng nhập Steam: Sai tên đăng nhập hoặc mật khẩu. Vui lòng kiểm tra lại thông tin trong cài đặt profile.");

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
                    string errorMessage;
                    if (initialRunResult.ExitCode == 5)
                    {
                        errorMessage = $"Cập nhật '{gameName}' (AppID: {specificAppId}) thất bại. Lỗi: Sai tên đăng nhập hoặc mật khẩu Steam. Vui lòng kiểm tra lại thông tin đăng nhập trong cài đặt profile.";
                    }
                    else
                    {
                        errorMessage = $"Cập nhật '{gameName}' (AppID: {specificAppId}) thất bại. Lý do: Exit Code: {initialRunResult.ExitCode}";
                    }
                    _logger.LogError("ExecuteProfileUpdateAsync: {Message}", errorMessage);
                    await SafeSendLogAsync(profile.Name, "Error", errorMessage);
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

                string errorMsg;
                if (initialRunResult.ExitCode == 5)
                {
                    errorMsg = $"Cập nhật {profile.Name} thất bại. Lỗi: Sai tên đăng nhập hoặc mật khẩu Steam. Vui lòng kiểm tra lại thông tin đăng nhập trong cài đặt profile.";
                }
                else
                {
                    errorMsg = $"Cập nhật {profile.Name} thất bại. Lý do: Exit Code: {initialRunResult.ExitCode}";
                }

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

        // Sửa phương thức RunSteamCmdProcessAsync để đảm bảo SteamCmd thực sự chạy
        private async Task<SteamCmdRunResult> RunSteamCmdProcessAsync(SteamCmdProfile profile, int profileId, List<string> appIdsToUpdate, bool forceValidate)
        {
            // Kiểm tra thư mục trước khi chạy
            if (!await CheckSteamDirectories(profile))
            {
                return new SteamCmdRunResult { Success = false, ExitCode = -1 };
            }

            Process steamCmdProcess = null;
            var runResult = new SteamCmdRunResult { Success = false, ExitCode = -1 };
            string steamCmdPath = GetSteamCmdPath();
            string steamCmdDir = Path.GetDirectoryName(steamCmdPath);
            string loginCommand = null;
            bool processStarted = false;
            bool updatingStarted = false;
            bool loginSuccessful = false;
            bool downloadStarted = false;
            bool alreadyUpToDate = false;
            bool processCompleted = false;
            bool hasError = false;
            int progressPercentage = 0;
            DateTime processStartTime = DateTime.Now;
            DateTime lastProgressUpdate = DateTime.Now;
            TimeSpan elapsedTime;
            const int PROGRESS_TIMEOUT_SECONDS = 300; // 5 phút timeout cho mỗi bước
            const int MIN_PROCESS_TIME_SECONDS = 10; // Giảm thời gian chờ tối thiểu xuống 10 giây nếu đã up to date
            System.Timers.Timer outputTimer = null;
            StringBuilder outputBuffer = new StringBuilder();
            StringBuilder errorBuffer = new StringBuilder();

            _lastRunHadLoginError = false;

            try
            {
                // Kiểm tra và tạo thư mục steamcmd nếu chưa tồn tại
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd: {Directory}", steamCmdDir);
                }

                // Kiểm tra SteamCMD tồn tại
                if (!File.Exists(steamCmdPath))
                {
                    await LogOperationAsync(profile.Name, "Error", $"File SteamCMD không tồn tại: {steamCmdPath}. Đang thử cài đặt lại...", "Error");
                    await InstallSteamCmd();
                    if (!File.Exists(steamCmdPath))
                    {
                        runResult.ExitCode = -99;
                        return runResult;
                    }
                }

                // Kiểm tra thư mục steamapps
                string steamappsDir = Path.Combine(steamCmdDir, "steamapps");
                if (!Directory.Exists(steamappsDir))
                {
                    Directory.CreateDirectory(steamappsDir);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đã tạo thư mục steamapps: {steamappsDir}");
                }

                // Chuẩn bị thông tin đăng nhập
                try {
                    // Tìm tài khoản phù hợp cho AppID đầu tiên
                    string appIdForAccount = appIdsToUpdate.FirstOrDefault() ?? profile.AppID;

                    _logger.LogInformation("RunSteamCmdProcessAsync: Tìm tài khoản cho AppID {AppId}", appIdForAccount);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang tìm tài khoản Steam cho AppID {appIdForAccount}...");

                    // Thông tin tài khoản từ SteamAccounts (đã giải mã trong GetSteamAccountForAppId)
                    var (accountUsername, accountPassword) = await _profileService.GetSteamAccountForAppId(appIdForAccount);

                    if (!string.IsNullOrEmpty(accountUsername) && !string.IsNullOrEmpty(accountPassword))
                    {
                        // QUAN TRỌNG: Kiểm tra xem accountPassword có vẻ như đã bị mã hóa không
                        if (accountPassword.Length > 30 && accountPassword.Contains("/") && accountPassword.Contains("+"))
                        {
                            try
                            {
                                // Cố gắng giải mã lại một lần nữa
                                string decryptedPass = _encryptionService.Decrypt(accountPassword);
                                _logger.LogDebug("Đã giải mã mật khẩu từ {OrigLength} thành {NewLength} ký tự", 
                                    accountPassword.Length, decryptedPass.Length);
                                accountPassword = decryptedPass;
                            }
                            catch
                            {
                                // Nếu không giải mã được, vẫn sử dụng giá trị hiện tại
                                _logger.LogWarning("Không thể giải mã lại mật khẩu, có thể đã giải mã rồi");
                            }
                        }
                        
                        // Thoát các ký tự đặc biệt trong mật khẩu để tránh lỗi trên command line
                        if (accountPassword.Contains("@") || accountPassword.Contains("#") || 
                            accountPassword.Contains("!") || accountPassword.Contains("&") ||
                            accountPassword.Contains("<") || accountPassword.Contains(">") ||
                            accountPassword.Contains("|") || accountPassword.Contains("^"))
                        {
                            // Đối với Windows, cách tốt nhất là đặt mật khẩu trong dấu ngoặc kép
                            // và thoát các ký tự đặc biệt bằng dấu ^
                            string originalPassword = accountPassword;
                            
                            // Kiểm tra nếu đã có dấu ngoặc kép rồi thì bỏ đi trước khi xử lý
                            if (accountPassword.StartsWith("\"") && accountPassword.EndsWith("\"") && accountPassword.Length >= 2)
                            {
                                accountPassword = accountPassword.Substring(1, accountPassword.Length - 2);
                            }
                            
                            accountPassword = accountPassword
                                .Replace("^", "^^")  // ^ phải được thoát đầu tiên
                                .Replace("&", "^&")
                                .Replace("|", "^|")
                                .Replace("<", "^<")
                                .Replace(">", "^>")
                                .Replace("(", "^(")
                                .Replace(")", "^)");
                                
                            // Thêm dấu ngoặc kép
                            accountPassword = $"\"{accountPassword}\"";
                            
                            _logger.LogDebug("Đã thoát ký tự đặc biệt trong mật khẩu để tránh lỗi command line");
                        }
                        
                        // Tạo lệnh đăng nhập với mật khẩu đã được giải mã
                        loginCommand = $"+login {accountUsername} {accountPassword}";
                        _logger.LogInformation("Sử dụng tài khoản {Username} từ SteamAccounts", accountUsername);
                        await SafeSendLogAsync(profile.Name, "Info", $"Sử dụng tài khoản {accountUsername} từ SteamAccounts");

                        // Log thêm để kiểm tra
                        _logger.LogDebug("Login command: {LoginCommand}",
                            $"+login {accountUsername} ***PASSWORD***");
                    }
                    else if (!string.IsNullOrEmpty(profile.SteamUsername) && !string.IsNullOrEmpty(profile.SteamPassword))
                    {
                        // Sử dụng tài khoản từ profile (backup)
                        try
                        {
                            string username = _encryptionService.Decrypt(profile.SteamUsername);
                            string password = _encryptionService.Decrypt(profile.SteamPassword);
                            
                            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                            {
                                // Thoát các ký tự đặc biệt trong mật khẩu để tránh lỗi trên command line
                                if (password.Contains("@") || password.Contains("#") || 
                                    password.Contains("!") || password.Contains("&") ||
                                    password.Contains("<") || password.Contains(">") ||
                                    password.Contains("|") || password.Contains("^"))
                                {
                                    // Đối với Windows, cách tốt nhất là đặt mật khẩu trong dấu ngoặc kép
                                    // và thoát các ký tự đặc biệt bằng dấu ^
                                    
                                    // Kiểm tra nếu đã có dấu ngoặc kép rồi thì bỏ đi trước khi xử lý
                                    if (password.StartsWith("\"") && password.EndsWith("\"") && password.Length >= 2)
                                    {
                                        password = password.Substring(1, password.Length - 2);
                                    }
                                    
                                    password = password
                                        .Replace("^", "^^")  // ^ phải được thoát đầu tiên
                                        .Replace("&", "^&")
                                        .Replace("|", "^|")
                                        .Replace("<", "^<")
                                        .Replace(">", "^>")
                                        .Replace("(", "^(")
                                        .Replace(")", "^)");
                                    
                                    // Thêm dấu ngoặc kép
                                    password = $"\"{password}\"";
                                    
                                    _logger.LogDebug("Đã thoát ký tự đặc biệt trong mật khẩu từ profile để tránh lỗi command line");
                                }
                                
                                loginCommand = $"+login {username} {password}";
                                _logger.LogWarning("RunSteamCmdProcessAsync: Không tìm thấy tài khoản trong SteamAccounts, sử dụng tài khoản từ profile");
                                await SafeSendLogAsync(profile.Name, "Warning", "Không tìm thấy tài khoản trong SteamAccounts, sử dụng tài khoản từ profile");
                            }
                            else
                            {
                                throw new Exception("Tên đăng nhập hoặc mật khẩu trong profile trống sau khi giải mã");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "RunSteamCmdProcessAsync: Lỗi khi giải mã thông tin đăng nhập từ profile");
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã thông tin đăng nhập: {ex.Message}");
                            _lastRunHadLoginError = true;
                            runResult.ExitCode = -97;
                            return runResult;
                        }
                    }
                    else
                    {
                        // Không có tài khoản nào khả dụng
                        _logger.LogError("RunSteamCmdProcessAsync: Không tìm thấy tài khoản cho AppID {AppId} trong SteamAccounts hoặc profile", appIdForAccount);
                        await SafeSendLogAsync(profile.Name, "Error", $"Không tìm thấy tài khoản Steam cho AppID {appIdForAccount} trong SteamAccounts hoặc profile");
                        _lastRunHadLoginError = true;
                        runResult.ExitCode = -96;
                        return runResult;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "RunSteamCmdProcessAsync: Lỗi khi tìm và chuẩn bị thông tin đăng nhập");
                    await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chuẩn bị đăng nhập: {ex.Message}");
                    _lastRunHadLoginError = true;
                    runResult.ExitCode = -95;
                    return runResult;
                }

                // Xây dựng command line
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
                string safeArguments = SanitizeLogMessage(arguments);
                _logger.LogInformation("Chạy SteamCMD cho '{ProfileName}' với tham số: {SafeArguments}", profile.Name, safeArguments);

                // Thông báo đang bắt đầu SteamCMD
                await SafeSendLogAsync(profile.Name, "Info", "Đang khởi động SteamCMD...");

                // Thay đổi cách khởi tạo process
                try {
                    // Kiểm tra xem steamcmd.exe có tồn tại không
                    if (!File.Exists(steamCmdPath))
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"Không tìm thấy file SteamCMD: {steamCmdPath}");
                        return new SteamCmdRunResult { Success = false, ExitCode = -200 };
                    }
                    
                    // Đảm bảo kill hết các tiến trình cũ
                    await KillAllSteamCmdProcessesAsync();
                    await Task.Delay(3000); // Tăng thời gian đợi từ 2000 lên 3000ms để đảm bảo tiến trình đã kết thúc hoàn toàn
                    
                    // Thêm kiểm tra file lock
                    string lockFilePath = Path.Combine(Path.GetDirectoryName(steamCmdPath), "steam.lock");
                    if (File.Exists(lockFilePath))
                    {
                        try
                        {
                            File.Delete(lockFilePath);
                            _logger.LogInformation("Đã xóa file lock: {FilePath}", lockFilePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Không thể xóa file lock: {Error}", ex.Message);
                            // Đợi thêm thời gian nếu không xóa được file lock
                            await Task.Delay(2000);
                        }
                    }
                    
                    // Tạo mới process
                    steamCmdProcess = new Process();
                    steamCmdProcess.StartInfo.FileName = steamCmdPath;
                    
                    // Xử lý arguments dài bằng cách tạo batch file thay vì sử dụng anonymous
                    if (arguments.Length > 2000)
                    {
                        string batchFilePath = Path.Combine(steamCmdDir, $"run_steam_{profileId}_{DateTime.Now.Ticks}.bat");
                        await File.WriteAllTextAsync(batchFilePath, $"\"{steamCmdPath}\" {arguments}");
                        steamCmdProcess.StartInfo.FileName = batchFilePath;
                        steamCmdProcess.StartInfo.Arguments = "";
                        await SafeSendLogAsync(profile.Name, "Info", $"Command quá dài, đang sử dụng batch file: {batchFilePath}");
                    }
                    else
                    {
                        steamCmdProcess.StartInfo.Arguments = arguments;
                    }
                    
                    steamCmdProcess.StartInfo.WorkingDirectory = Path.GetDirectoryName(steamCmdPath);
                    steamCmdProcess.StartInfo.UseShellExecute = false;
                    steamCmdProcess.StartInfo.RedirectStandardOutput = true;
                    steamCmdProcess.StartInfo.RedirectStandardError = true;
                    steamCmdProcess.StartInfo.RedirectStandardInput = true;
                    steamCmdProcess.StartInfo.CreateNoWindow = true;
                    steamCmdProcess.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    steamCmdProcess.StartInfo.StandardErrorEncoding = Encoding.UTF8;
                    steamCmdProcess.EnableRaisingEvents = true;
                    
                    // Đảm bảo thư mục làm việc tồn tại
                    if (!Directory.Exists(steamCmdProcess.StartInfo.WorkingDirectory))
                    {
                        Directory.CreateDirectory(steamCmdProcess.StartInfo.WorkingDirectory);
                    }
                    
                    _steamCmdProcesses[profileId] = steamCmdProcess;
                    
                    // Sử dụng extension method SafeStart để bắt các lỗi khi khởi động
                    try {
                        steamCmdProcess.SafeStart();
                    } catch (Exception startEx) {
                        _logger.LogError(startEx, "Lỗi khi khởi động SteamCMD: {Message}", startEx.Message);
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi khởi động SteamCMD: {startEx.Message}");
                        
                        // Kiểm tra nếu là lỗi "The system cannot execute the specified program"
                        if (startEx.Message.Contains("The system cannot execute the specified program", StringComparison.OrdinalIgnoreCase))
                        {
                            // Dọn dẹp triệt để
                            await KillAllSteamCmdProcessesAsync();
                            await Task.Delay(5000); // Đợi lâu hơn
                            await CleanupSteamCmdTemporaryFiles();
                            
                            // Thực hiện các lệnh dọn dẹp hệ thống
                            try 
                            {
                                _logger.LogWarning("Đang thực hiện dọn dẹp triệt để sau lỗi 'The system cannot execute the specified program'");
                                CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe /T", 10000);
                                CmdHelper.RunCommand("taskkill /F /IM steamservice.exe /T", 10000);
                                await Task.Delay(5000);
                            } 
                            catch { }
                        }
                        
                        return new SteamCmdRunResult { Success = false, ExitCode = -201 };
                    }
                    
                    profile.Pid = steamCmdProcess.Id;
                    await _profileService.UpdateProfile(profile);

                var recentOutputMessages = new ConcurrentDictionary<string, byte>();

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
                    outputTimer.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi cấu hình SteamCMD process: {Message}", ex.Message);
                    await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi cấu hình SteamCMD process: {ex.Message}");
                    return new SteamCmdRunResult { Success = false, ExitCode = -1 };
                }

                // Xử lý output
                steamCmdProcess.OutputDataReceived += async (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e?.Data)) return;

                    string line = e.Data.Trim();
                    
                    // Kiểm tra các trạng thái quan trọng
                    if (line.Contains("already up to date", StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyUpToDate = true;
                        processCompleted = true;
                        
                        // Trích xuất AppID từ thông báo (nếu có)
                        string appId = profile.AppID;
                        var appIdMatch = Regex.Match(line, @"App '([0-9]+)'");
                        if (appIdMatch.Success)
                        {
                            appId = appIdMatch.Groups[1].Value;
                        }
                        
                        // Lấy tên game
                        string gameName = await GetGameNameFromAppId(appId);
                        
                        await SafeSendLogAsync(profile.Name, "Success", $"Thành công: {gameName} (AppID: {appId}) đã được cập nhật (Already up to date)");
                    }
                    else if (line.Contains("Success!", StringComparison.OrdinalIgnoreCase))
                    {
                        processCompleted = true;
                        
                        // Trích xuất AppID từ thông báo (nếu có)
                        string appId = profile.AppID;
                        Match appIdMatch = Regex.Match(line, @"App '([0-9]+)'");
                        if (appIdMatch.Success)
                        {
                            appId = appIdMatch.Groups[1].Value;
                        }
                        
                        // Kiểm tra nếu có thông báo fully installed
                        bool isFullyInstalled = line.Contains("fully installed", StringComparison.OrdinalIgnoreCase);
                        
                        // Lấy tên game
                        string gameName = await GetGameNameFromAppId(appId);
                        
                        string message = isFullyInstalled 
                            ? $"Thành công: {gameName} (AppID: {appId}) đã được cài đặt hoàn tất"
                            : $"Thành công: {gameName} (AppID: {appId}) đã được cập nhật thành công";
                            
                        await SafeSendLogAsync(profile.Name, "Success", message);
                    }
                    else if (line.Contains("The system cannot execute the specified program", StringComparison.OrdinalIgnoreCase))
                    {
                        hasError = true;
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi SteamCMD: The system cannot execute the specified program");
                    }
                    else if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || 
                             line.Contains("FAILED", StringComparison.OrdinalIgnoreCase))
                    {
                        // Xử lý lỗi Rate Limit Exceeded (đăng nhập quá nhiều lần)
                        if (line.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase))
                        {
                            hasError = true;
                            string errorMessage = "Lỗi: Đã đăng nhập Steam quá nhiều lần (Rate Limit Exceeded). Vui lòng đợi 5 phút rồi thử lại.";
                            await SafeSendLogAsync(profile.Name, "Error", errorMessage);
                            _logger.LogError("Steam Rate Limit Exceeded cho profile '{ProfileName}'. Cần đợi 5 phút.", profile.Name);
                        }
                        // Bỏ qua một số thông báo lỗi thông thường không ảnh hưởng
                        else if (!line.Contains("Failed to load cell") && 
                            !line.Contains("Failed to init SDL") && 
                            !line.Contains("Failed to initialize GL"))
                        {
                            hasError = true;
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi: {line}");
                        }
                    }
                    else if (line.Contains("Logging in user") || line.Contains("Logged in OK") || (line.Contains("OK") && line.Contains("Logging in")))
                    {
                        loginSuccessful = true;
                        
                        // Thêm thông báo đăng nhập thành công vào log
                        await SafeSendLogAsync(profile.Name, "Success", "Đã đăng nhập thành công vào Steam");
                        _logger.LogInformation("Đăng nhập Steam thành công cho profile '{ProfileName}'", profile.Name);
                    }
                    else if (line.Contains("Loading Steam API") || line.Contains("Steam Console Client"))
                    {
                        processStarted = true;
                    }
                    else if (line.Contains("Downloading update") || line.Contains("Update state"))
                    {
                        updatingStarted = true;
                        downloadStarted = true;
                        
                        // Trích xuất tiến trình tải về (nếu có)
                        var progressMatch = Regex.Match(line, @"(\d+(?:\.\d+)?)%");
                        if (progressMatch.Success && float.TryParse(progressMatch.Groups[1].Value, out float progress))
                        {
                            progressPercentage = (int)progress;
                            
                            // Trích xuất AppID từ dòng log (nếu có)
                            string appId = profile.AppID;
                            var appIdMatch = Regex.Match(line, @"App '([0-9]+)'");
                            if (appIdMatch.Success)
                            {
                                appId = appIdMatch.Groups[1].Value;
                            }
                            
                            // Lấy tên game (chỉ làm 1 lần và cache lại để tránh gọi API quá nhiều)
                            string gameName = await GetGameNameFromAppId(appId);
                            
                            await SafeSendLogAsync(profile.Name, "Progress", $"Đang tải về {gameName} (AppID: {appId}): {progressPercentage}%");
                        }
                        else
                        {
                            await SafeSendLogAsync(profile.Name, "Info", $"Đang tải về cập nhật: {line}");
                        }
                    }
                    else if (line.Contains("Unloading Steam API"))
                    {
                        // Thường xuất hiện khi hoàn tất thành công
                        if (alreadyUpToDate || processCompleted)
                        {
                            await SafeSendLogAsync(profile.Name, "Info", "Steam API đã dỡ, quá trình hoàn tất");
                        }
                    }

                    // Cập nhật thời gian của lần cập nhật cuối
                    lastProgressUpdate = DateTime.Now;
                };

                // Xử lý error output
                steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                {
                    if (string.IsNullOrEmpty(e.Data)) return;
                    string errorLine = e.Data.Trim();

                    lock (errorBuffer)
                    {
                        errorBuffer.AppendLine(errorLine);
                    }

                    _logger.LogError("SteamCMD Error ({ProfileName}): {Data}", profile.Name, errorLine);
                    await SafeSendLogAsync(profile.Name, "Error", $"LỖI SteamCMD: {errorLine}");
                };

                // Bắt đầu process
                steamCmdProcess.Start();
                steamCmdProcess.BeginOutputReadLine();
                steamCmdProcess.BeginErrorReadLine();
                outputTimer.Start();
                
                // Thông báo bắt đầu cài đặt
                string appIdsStr = string.Join(", ", appIdsToUpdate);
                string gameNames = string.Empty;
                
                try 
                {
                    var appNames = await GetAppNamesAsync(steamappsDir, appIdsToUpdate);
                    if (appNames != null && appNames.Count > 0)
                    {
                        gameNames = string.Join(", ", appNames.Values);
                        await SafeSendLogAsync(profile.Name, "Info", $"Bắt đầu cài đặt/cập nhật: {gameNames} (AppID: {appIdsStr})");
                    }
                    else
                    {
                        await SafeSendLogAsync(profile.Name, "Info", $"Bắt đầu cài đặt/cập nhật AppID: {appIdsStr}");
                    }
                }
                catch
                {
                    await SafeSendLogAsync(profile.Name, "Info", $"Bắt đầu cài đặt/cập nhật AppID: {appIdsStr}");
                }

                // Đợi process hoàn thành
                await steamCmdProcess.WaitForExitAsync();
                elapsedTime = DateTime.Now - processStartTime;

                // Dọn dẹp file batch nếu đã tạo
                if (steamCmdProcess.StartInfo.Arguments == "" && steamCmdProcess.StartInfo.FileName != steamCmdPath)
                {
                    try
                    {
                        string batchFilePath = steamCmdProcess.StartInfo.FileName;
                        if (File.Exists(batchFilePath))
                        {
                            File.Delete(batchFilePath);
                            _logger.LogInformation("Đã xóa file batch tạm: {FilePath}", batchFilePath);
                        }
                }
                catch (Exception ex)
                {
                        _logger.LogWarning("Không thể xóa file batch tạm: {Message}", ex.Message);
                    }
                }

                // Kiểm tra kết quả theo thứ tự ưu tiên
                if (hasError && !processCompleted)
                {
                    if (errorBuffer.ToString().Contains("The system cannot execute the specified program"))
                    {
                        _logger.LogError("Lỗi SteamCMD: The system cannot execute the specified program");
                        await SafeSendLogAsync(profile.Name, "Error", "Lỗi SteamCMD: The system cannot execute the specified program");
                    runResult.Success = false;
                        runResult.ExitCode = -99;
                        
                        // Dọn dẹp tiến trình steamcmd đang chạy
                        try 
                        {
                            foreach (var proc in Process.GetProcessesByName("steamcmd"))
                            {
                                try { proc.Kill(); } catch { }
                            }
                            await Task.Delay(5000); // Tăng thời gian đợi từ 2000 lên 5000ms
                            
                            // Dọn dẹp file tạm triệt để
                            await CleanupSteamCmdTemporaryFiles();
                            
                            // Xóa bất kỳ file tmp nào trong thư mục root
                            try 
                            {
                                var rootTempFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.tmp");
                                foreach (var tempFile in rootTempFiles)
                                {
                                    try { File.Delete(tempFile); } catch { }
                                }
                            }
                            catch { }
                        }
                        catch (Exception ex) 
                        {
                            _logger.LogWarning("Không thể dọn dẹp tiến trình: {Error}", ex.Message);
                        }
                        
                        return runResult;
                    }
                    
                    _logger.LogError("SteamCMD gặp lỗi trong quá trình thực thi");
                    await SafeSendLogAsync(profile.Name, "Error", "SteamCMD gặp lỗi trong quá trình thực thi");
                    runResult.Success = false;
                    runResult.ExitCode = steamCmdProcess.ExitCode;
                    return runResult;
                }
                
                if (alreadyUpToDate)
                {
                    // Nếu đã up to date và thời gian chạy > MIN_PROCESS_TIME_SECONDS
                    if (elapsedTime.TotalSeconds >= MIN_PROCESS_TIME_SECONDS)
                    {
                        _logger.LogInformation("Ứng dụng đã được cập nhật (Already up to date)");
                        await SafeSendLogAsync(profile.Name, "Success", "Ứng dụng đã được cập nhật (Already up to date)");
                        runResult.Success = true;
                        runResult.ExitCode = 0; // Coi như Exit Code 0 nếu already up to date
                    return runResult;
                    }
                }

                // Kiểm tra các điều kiện thành công
                if (processCompleted && !hasError)
                {
                    if (elapsedTime.TotalSeconds >= MIN_PROCESS_TIME_SECONDS)
                    {
                        _logger.LogInformation("Cập nhật hoàn tất thành công");
                        await SafeSendLogAsync(profile.Name, "Success", "Cập nhật hoàn tất thành công");
                        runResult.Success = true;
                        runResult.ExitCode = 0; // Coi như Exit Code 0 nếu cập nhật thành công
                        return runResult;
                    }
                    }

                // Kiểm tra thời gian chạy quá ngắn
                if (elapsedTime.TotalSeconds < MIN_PROCESS_TIME_SECONDS)
                    {
                    _logger.LogWarning($"Quá trình kết thúc quá nhanh ({elapsedTime.TotalSeconds:F1} giây)");
                    await SafeSendLogAsync(profile.Name, "Warning", $"Quá trình kết thúc quá nhanh ({elapsedTime.TotalSeconds:F1} giây)");
                        runResult.Success = false;
                    runResult.ExitCode = -1;
                        return runResult;
                    }

                // Kiểm tra các trường hợp khác
                if (steamCmdProcess.ExitCode == 0)
                    {
                    if (!processStarted || !loginSuccessful)
                    {
                        _logger.LogWarning("Quá trình kết thúc với Exit Code 0 nhưng không hoàn tất các bước cần thiết");
                        await SafeSendLogAsync(profile.Name, "Warning", "Quá trình kết thúc với Exit Code 0 nhưng không hoàn tất các bước cần thiết");
                        runResult.Success = false;
                        runResult.ExitCode = -1;
                    }
                    else
                    {
                        _logger.LogInformation("Quá trình hoàn tất với Exit Code 0");
                        await SafeSendLogAsync(profile.Name, "Success", "Quá trình hoàn tất với Exit Code 0");
                        runResult.Success = true;
                        runResult.ExitCode = 0;
                    }
                }
                else
                {
                    _logger.LogError($"Quá trình kết thúc với Exit Code: {steamCmdProcess.ExitCode}");
                    await SafeSendLogAsync(profile.Name, "Error", $"Quá trình kết thúc với Exit Code: {steamCmdProcess.ExitCode}");
                    runResult.Success = false;
                    runResult.ExitCode = steamCmdProcess.ExitCode;
                }

                return runResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi không mong muốn trong RunSteamCmdProcessAsync: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi không mong muốn: {ex.Message}");
                runResult.Success = false;
                runResult.ExitCode = -1;
            }
            finally
            {
                if (outputTimer != null)
                {
                    outputTimer.Stop();
                    outputTimer.Dispose();
                }
                
                if (steamCmdProcess != null)
                {
                    try
                    {
                        if (!steamCmdProcess.HasExited)
                        {
                            steamCmdProcess.Kill();
                        }
                        steamCmdProcess.Dispose();
                    }
                    catch { }
                }
                
                _steamCmdProcesses.TryRemove(profileId, out _);
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
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                return false;
            }

            var profile = await _profileService.GetProfileById(profileId);
            if (profile == null)
            {
                _logger.LogError("RunSpecificAppAsync: Không tìm thấy profile ID {ProfileId} để chạy App ID {AppId}", profileId, appId);
                await SafeSendLogAsync($"Profile {profileId}", "Error", $"Không tìm thấy profile ID {profileId} để chạy App ID {appId}");
                return false;
            }

            // Kiểm tra debug trước
            await DebugCheckSteamAccountsForAppId(appId);

            // Kiểm tra xem có tài khoản phù hợp cho AppID này không
            var (foundUsername, foundPassword) = await _profileService.GetSteamAccountForAppId(appId);
            if (string.IsNullOrEmpty(foundUsername) || string.IsNullOrEmpty(foundPassword))
            {
                string errorMessage = $"Không tìm thấy tài khoản Steam phù hợp cho App ID {appId} trong SteamAccounts.";
                
                // Kiểm tra nếu profile có tài khoản
                if (!string.IsNullOrEmpty(profile.SteamUsername) && !string.IsNullOrEmpty(profile.SteamPassword))
                {
                    errorMessage += " Sẽ sử dụng tài khoản từ profile.";
                    await SafeSendLogAsync(profile.Name, "Warning", errorMessage);
                }
                else
                {
                    await SafeSendLogAsync(profile.Name, "Error", errorMessage + " Không thể tiếp tục vì profile không có tài khoản.");
                    return false;
                }
            }
            else
            {
                await SafeSendLogAsync(profile.Name, "Info", $"Tìm thấy tài khoản {foundUsername} phù hợp cho AppID {appId}");
            }

            // Kiểm tra xem đã có trong hàng đợi chưa
            if (await IsAlreadyInQueueAsync(profileId, appId))
            {
                _logger.LogInformation("RunSpecificAppAsync: AppID {AppId} đã có trong hàng đợi, bỏ qua", appId);
                
                // Cập nhật QueueService để hiển thị lại
                using (var scope = _serviceProvider.CreateScope())
                {
                    var queueService = scope.ServiceProvider.GetRequiredService<QueueService>();
                    await queueService.UpdateQueueStatusAsync();
                }
                
                return true;
            }
            
            // Thêm vào hàng đợi qua QueueService
            using (var scope = _serviceProvider.CreateScope())
            {
                var queueService = scope.ServiceProvider.GetRequiredService<QueueService>();
                bool addedToQueue = await queueService.AddToQueue(profileId, appId, appId == profile.AppID);
                
                if (!addedToQueue)
                {
                    _logger.LogWarning("RunSpecificAppAsync: Không thể thêm AppID {AppId} vào hàng đợi", appId);
                    await SafeSendLogAsync(profile.Name, "Warning", $"Không thể thêm AppID {appId} vào hàng đợi");
                    return false;
                }
            }

            _logger.LogInformation("RunSpecificAppAsync: Đã thêm AppID {AppId} vào hàng đợi", appId);
            
            // Lấy tên app để hiển thị log
            var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
            string appName = appInfo?.Name ?? $"AppID {appId}";
            await SafeSendLogAsync(profile.Name, "Info", $"Đã thêm '{appName}' (AppID: {appId}) vào hàng đợi cập nhật.");
            
            // Bắt đầu xử lý hàng đợi
            StartQueueProcessorIfNotRunning();
            
            // Cập nhật trạng thái profile
            profile.Status = "Running";
            profile.StartTime = DateTime.Now;
            await _profileService.UpdateProfile(profile);
            
            return true;
        }

        // Thêm phương thức debug để kiểm tra sự tồn tại của tài khoản Steam cho AppID
        private async Task<bool> DebugCheckSteamAccountsForAppId(string appId)
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var steamAccountService = scope.ServiceProvider.GetRequiredService<SteamAccountService>();
                    var accounts = await steamAccountService.GetAllAccountsAsync();

                    _logger.LogInformation("DEBUG: Đang kiểm tra {Count} tài khoản Steam cho AppID {AppId}", accounts.Count, appId);

                    foreach (var account in accounts)
                    {
                        var appIds = !string.IsNullOrEmpty(account.AppIds) ? 
                            account.AppIds.Split(',').Select(id => id.Trim()).ToList() : 
                            new List<string>();

                        _logger.LogInformation("DEBUG: Tài khoản {Username} có {Count} AppIDs: {AppIds}", 
                            account.Username, appIds.Count, string.Join(", ", appIds));

                        if (appIds.Contains(appId))
                        {
                            _logger.LogInformation("DEBUG: Tìm thấy AppID {AppId} trong tài khoản {Username}", 
                                appId, account.Username);
                            await SafeSendLogAsync("DEBUG", "Info", $"Tìm thấy AppID {appId} trong tài khoản {account.Username}");
                            return true;
                        }
                    }

                    _logger.LogWarning("DEBUG: Không tìm thấy AppID {AppId} trong bất kỳ tài khoản nào", appId);
                    await SafeSendLogAsync("DEBUG", "Warning", $"Không tìm thấy AppID {appId} trong bất kỳ tài khoản nào");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DEBUG: Lỗi khi kiểm tra tài khoản Steam cho AppID {AppId}", appId);
                return false;
            }
        }

        // Thêm phương thức kiểm tra xem đã có trong hàng đợi chưa
        private async Task<bool> IsAlreadyInQueueAsync(int profileId, string appId)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                try
                {
                    var queueService = scope.ServiceProvider.GetRequiredService<QueueService>();
                    return queueService.IsAlreadyInQueue(profileId, appId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra queue cho AppID {AppId}, ProfileId {ProfileId}", appId, profileId);
                    return false;
                }
            }
        }

        public async Task RunAllProfilesAsync()
        {
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                await SafeSendLogAsync("System", "Error", "Không thể thực hiện thao tác - License không hợp lệ");
                
                // Thông báo cho người dùng qua Hub
                try 
                {
                    await _hubContext.Clients.All.SendAsync("LicenseError", "License không hợp lệ. Vui lòng cập nhật License để tiếp tục sử dụng dịch vụ.");
                }
                catch { }
                
                return;
            }

            if (_isRunningAllProfiles)
            {
                _logger.LogWarning("RunAllProfilesAsync: Đang chạy tất cả profiles. Bỏ qua.");
                await SafeSendLogAsync("System", "Warning", "Đang chạy tất cả profiles. Bỏ qua yêu cầu mới.");
                return;
            }

            _isRunningAllProfiles = true;
            _currentProfileIndex = 0;
            _cancelAutoRun = false;
            var profiles = await _profileService.GetAllProfiles();

            if (profiles == null || profiles.Count == 0)
            {
                _logger.LogWarning("RunAllProfilesAsync: Không tìm thấy profiles nào để chạy");
                await SafeSendLogAsync("System", "Warning", "Không tìm thấy profiles nào để chạy");
                _isRunningAllProfiles = false;
                return;
            }

            _logger.LogInformation("RunAllProfilesAsync: Bắt đầu chạy {Count} profiles", profiles.Count);
            await SafeSendLogAsync("System", "Info", $"Bắt đầu chạy {profiles.Count} profiles");

            try
            {
                // Sắp xếp lại profile theo LastRun
                profiles = profiles.OrderBy(p => p.LastRun ?? DateTime.MinValue).ToList();
                
                // In danh sách profile sẽ được cập nhật
                foreach (var profile in profiles)
                {
                    string gameName = await GetGameNameFromAppId(profile.AppID);
                    await SafeSendLogAsync("System", "Info", $"Chuẩn bị cập nhật: {profile.Name} - {gameName} (AppID: {profile.AppID})");
                }
                
                foreach (var profile in profiles)
                {
                    if (_cancelAutoRun)
                    {
                        _logger.LogWarning("RunAllProfilesAsync: Đã hủy cập nhật tự động");
                        await SafeSendLogAsync("System", "Warning", "Đã hủy cập nhật tự động");
                        break;
                    }

                    _currentProfileIndex = profiles.IndexOf(profile);
                    
                    string gameName = await GetGameNameFromAppId(profile.AppID);
                    await SafeSendLogAsync("System", "Info", $"Đang cập nhật profile {_currentProfileIndex + 1}/{profiles.Count}: {profile.Name} - {gameName}");
                    
                    // Thêm profile vào hàng đợi
                    if (!await QueueProfileForUpdate(profile.Id))
                    {
                        _logger.LogError("RunAllProfilesAsync: Không thể thêm profile '{ProfileName}' vào hàng đợi", profile.Name);
                        await SafeSendLogAsync(profile.Name, "Error", "Không thể thêm profile vào hàng đợi");
                    }

                    // Đợi cho đến khi hàng đợi xử lý xong
                    while (_isProcessingQueue)
                    {
                        if (_cancelAutoRun) break;
                        await Task.Delay(1000);
                    }

                    if (_cancelAutoRun) break;
                    
                    // Đợi thêm một khoảng thời gian giữa các profile để đảm bảo hệ thống ổn định
                    await Task.Delay(5000);
                }

                _lastAutoRunTime = DateTime.Now;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RunAllProfilesAsync: Lỗi khi chạy tất cả profiles");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chạy tất cả profiles: {ex.Message}");
            }
            finally
            {
                _isRunningAllProfiles = false;
            }

            _logger.LogInformation("RunAllProfilesAsync: Hoàn tất chạy tất cả profiles");
            await SafeSendLogAsync("System", "Info", "Hoàn tất chạy tất cả profiles");
        }


        public async Task StopAllProfilesAsync()
        {
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                return;
            }

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
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                return false;
            }

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
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                return false;
            }

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
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                return;
            }

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

        // Thêm phương thức mới để ghi log nhất quán
        private async Task LogOperationAsync(string profileName, string status, string message, string logType = "Info")
        {
            try
            {
                // Convert Warning to Error for simpler status display
                if (status.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                {
                    status = "Error";
                }
                
                // Convert Info to appropriate status based on message content
                if (status.Equals("Info", StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Contains("thành công", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("hoàn tất", StringComparison.OrdinalIgnoreCase) ||
                        message.Contains("đã xong", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "Success";
                    }
                    else if (message.Contains("lỗi", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("thất bại", StringComparison.OrdinalIgnoreCase) ||
                             message.Contains("không thể", StringComparison.OrdinalIgnoreCase))
                    {
                        status = "Error";
                    }
                    else
                    {
                        // Skip non-critical Info logs
                        return;
                    }
                }

                _logBuffer.Enqueue((status, profileName, message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm log vào buffer: {Message}", ex.Message);
            }
        }

        private async Task ProcessLogBufferAsync()
        {
            try
            {
                var logBatch = new List<(string status, string profileName, string message)>();

                while (logBatch.Count < LOG_BATCH_SIZE && _logBuffer.TryDequeue(out var logEntry))
                {
                    // Skip if message is empty or matches any of the skip patterns
                    if (string.IsNullOrWhiteSpace(logEntry.message) || ShouldSkipLog(logEntry.message))
                    {
                        continue;
                    }

                    // Lọc thông tin nhạy cảm trước khi thêm vào batch
                    var sanitizedMessage = SanitizeLogMessage(logEntry.message);

                    // Đảm bảo lỗi đăng nhập luôn được hiển thị ưu tiên
                    if (sanitizedMessage.Contains("Lỗi đăng nhập") || sanitizedMessage.Contains("ERROR (Invalid Password)"))
                    {
                        logEntry.status = "Error";
                        if (!sanitizedMessage.Contains("Vui lòng kiểm tra lại"))
                        {
                            sanitizedMessage = "Lỗi đăng nhập: Sai tên đăng nhập hoặc mật khẩu Steam. Vui lòng kiểm tra lại thông tin đăng nhập.";
                        }
                    }

                    logBatch.Add((logEntry.status, logEntry.profileName, sanitizedMessage));
                }

                if (logBatch.Count == 0) return;

                // Xử lý batch logs
                foreach (var (status, profileName, message) in logBatch)
                {
                    string displayProfileName = string.IsNullOrWhiteSpace(profileName) ? "System" : profileName;
                    
                    // Match the original style
                    var logObject = new
                    {
                        timestamp = DateTime.Now,
                        profileName = displayProfileName,
                        status = status,
                        message = message,
                        statusClass = status.ToLower() switch
                        {
                            "success" => "bg-success-subtle text-success-emphasis rounded-1 px-2 py-1 small",
                            "error" => "bg-danger-subtle text-danger-emphasis rounded-1 px-2 py-1 small",
                            _ => "bg-info-subtle text-info-emphasis rounded-1 px-2 py-1 small"
                        },
                        statusIcon = status.ToLower() switch
                        {
                            "success" => "✓",
                            "error" => "✕",
                            _ => "•"
                        },
                        statusText = status
                    };

                    // Gửi log tới SignalR hub để hiển thị real-time
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", logObject);

                    // Ghi log vào LogService để lưu lịch sử
                    string logLevel = status.ToUpper() switch
                    {
                        "SUCCESS" => "SUCCESS",
                        "ERROR" => "ERROR",
                        _ => "INFO"
                    };

                    // Thêm timestamp vào message để dễ theo dõi
                    string timestampedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";

                    // Ghi vào LogService với đầy đủ thông tin
                    _logService.AddLog(logLevel, timestampedMessage, displayProfileName, status);

                    // Ghi log vào file
                    var logEntry = new LogEntry(DateTime.Now, displayProfileName, status, message);
                    SaveLogToFile(logEntry);
                }

                // Ghi logs vào console trong một batch
                var logMessages = logBatch.Select(l => 
                    $"[{l.status}] [{(string.IsNullOrWhiteSpace(l.profileName) ? "System" : l.profileName)}] {l.message}");
                
                _logger.LogInformation(string.Join(Environment.NewLine, logMessages));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý log buffer");
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources
                    _scheduleTimer?.Dispose();
                    _hubMessageCleanupTimer?.Dispose();
                    _logProcessTimer?.Dispose();

                    // Dispose all running processes
                    foreach (var process in _steamCmdProcesses.Values)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                            process.Dispose();
                        }
                        catch (Exception) { }
                    }
                    _steamCmdProcesses.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SteamCmdService()
        {
            Dispose(false);
        }

        private async Task<bool> CheckSteamDirectories(SteamCmdProfile profile)
        {
            try
            {
                string steamCmdPath = GetSteamCmdPath();
                string steamCmdDir = Path.GetDirectoryName(steamCmdPath);
                string steamappsDir = profile?.InstallDirectory;

                if (!Directory.Exists(steamCmdDir))
                {
                    await SafeSendLogAsync("System", "Error", $"Thư mục SteamCMD không tồn tại: {steamCmdDir}");
                    _logger.LogError("Thư mục SteamCMD không tồn tại: {0}", steamCmdDir);
                    return false;
                }

                if (!string.IsNullOrEmpty(steamappsDir) && !Directory.Exists(steamappsDir))
                {
                    await SafeSendLogAsync("System", "Error", $"Thư mục cài đặt game không tồn tại: {steamappsDir}");
                    _logger.LogError("Thư mục cài đặt game không tồn tại: {0}", steamappsDir);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra thư mục Steam");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi kiểm tra thư mục Steam: {ex.Message}");
                return false;
            }
        }

        #region SteamCmd Installation and Path Management
        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            return Path.Combine(steamCmdDir, "steamcmd.exe");
        }

        public Task<bool> IsSteamCmdInstalled()
        {
            string steamCmdPath = GetSteamCmdPath();
            if (!File.Exists(steamCmdPath))
            {
                return Task.FromResult(false);
            }

            // Kiểm tra kích thước file
            try
            {
                FileInfo fileInfo = new FileInfo(steamCmdPath);
                if (fileInfo.Length < 10000) // 10KB
                {
                    return Task.FromResult(false);
                }
            }
            catch
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public async Task InstallSteamCmd()
        {
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                return;
            }

            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");
            string steamCmdPath = GetSteamCmdPath();
            string[] downloadUrls = new[]
            {
                "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip",
                "https://media.steampowered.com/installer/steamcmd.zip"
            };

            try
            {
                await SafeSendLogAsync("System", "Info", "Bắt đầu quá trình cài đặt SteamCMD...");
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs);

                // Xóa file steamcmd.exe cũ nếu tồn tại
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
                }

                // Tạo thư mục steamcmd nếu chưa tồn tại
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd: {Directory}", steamCmdDir);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamcmd: {steamCmdDir}");
                }

                bool downloadSuccess = false;
                Exception lastException = null;

                // Thử tải từ các URL khác nhau
                foreach (var downloadUrl in downloadUrls)
                {
                    try
                    {
                        using (var httpClient = new HttpClient())
                        {
                            httpClient.Timeout = TimeSpan.FromMinutes(5);
                            await SafeSendLogAsync("System", "Info", $"Đang tải SteamCMD từ {downloadUrl}...");
                            _logger.LogInformation("Bắt đầu tải SteamCMD từ {Url}", downloadUrl);

                            if (File.Exists(zipPath))
                            {
                                try { File.Delete(zipPath); } catch { }
                            }

                            var response = await httpClient.GetAsync(downloadUrl);
                            response.EnsureSuccessStatusCode();
                            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                await response.Content.CopyToAsync(fs);
                            }
                            downloadSuccess = true;
                            await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastException = ex;
                        _logger.LogWarning(ex, "Không thể tải SteamCMD từ {Url}, thử URL tiếp theo...", downloadUrl);
                        await SafeSendLogAsync("System", "Warning", $"Không thể tải từ {downloadUrl}, thử nguồn khác...");
                        continue;
                    }
                }

                if (!downloadSuccess)
                {
                    throw new Exception($"Không thể tải SteamCMD từ tất cả các nguồn. Lỗi cuối cùng: {lastException?.Message}");
                }

                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(3000);

                // Giải nén file zip
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
                finally
                {
                    try { File.Delete(zipPath); } catch { }
                }

                // Kiểm tra file sau khi giải nén
                if (!File.Exists(steamCmdPath))
                {
                    throw new Exception("Cài đặt thất bại. Không tìm thấy steamcmd.exe sau khi giải nén.");
                }

                // Kiểm tra kích thước file
                FileInfo fileInfo = new FileInfo(steamCmdPath);
                if (fileInfo.Length < 10000) // 10KB
                {
                    throw new Exception("File steamcmd.exe được tạo nhưng có kích thước không hợp lệ (< 10KB).");
                }

                // Kiểm tra xem steamcmd có chạy được không
                await SafeSendLogAsync("System", "Info", "Đang kiểm tra SteamCMD...");
                var testProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = steamCmdPath,
                        Arguments = "+quit",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                try
                {
                    testProcess.Start();
                    await testProcess.WaitForExitAsync();
                    if (testProcess.ExitCode != 0)
                    {
                        throw new Exception($"SteamCMD test failed with exit code: {testProcess.ExitCode}");
                    }
                    await SafeSendLogAsync("System", "Success", "Kiểm tra SteamCMD thành công.");
                }
                catch (Exception ex)
                {
                    throw new Exception($"Không thể chạy SteamCMD để kiểm tra: {ex.Message}");
                }
                finally
                {
                    testProcess.Dispose();
                }

                _logger.LogInformation("Đã cài đặt và xác thực SteamCMD thành công.");
                await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình cài đặt SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi nghiêm trọng khi cài đặt SteamCMD: {ex.Message}");
                throw;
            }
        }
        #endregion

        #region SteamCMD Management
        
        /// <summary>
        /// Dọn dẹp các file tạm thời của SteamCMD để tránh xung đột
        /// </summary>
        private async Task CleanupSteamCmdTemporaryFiles()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            
            try
            {
                // Xóa file lock
                string lockFilePath = Path.Combine(steamCmdDir, "steam.lock");
                if (File.Exists(lockFilePath))
                {
                    try 
                    { 
                        File.Delete(lockFilePath);
                        _logger.LogInformation("Đã xóa file lock: {FilePath}", lockFilePath);
                    }
                    catch (Exception ex) 
                    { 
                        _logger.LogWarning("Không thể xóa file lock: {Error}", ex.Message);
                    }
                }
                
                // Xóa file session
                string sessionFilePath = Path.Combine(steamCmdDir, "session_client.xml");
                if (File.Exists(sessionFilePath))
                {
                    try 
                    { 
                        File.Delete(sessionFilePath);
                        _logger.LogInformation("Đã xóa file session: {FilePath}", sessionFilePath);
                    }
                    catch (Exception ex) 
                    { 
                        _logger.LogWarning("Không thể xóa file session: {Error}", ex.Message);
                    }
                }
                
                // Xóa file temp
                var tempFiles = Directory.GetFiles(steamCmdDir, "*.tmp");
                foreach (var tempFile in tempFiles)
                {
                    try
                    {
                        File.Delete(tempFile);
                        _logger.LogInformation("Đã xóa file tạm: {FilePath}", tempFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Không thể xóa file tạm {FilePath}: {Error}", tempFile, ex.Message);
                    }
                }
                
                // Xóa các file .pid
                var pidFiles = Directory.GetFiles(steamCmdDir, "*.pid");
                foreach (var pidFile in pidFiles)
                {
                    try
                    {
                        File.Delete(pidFile);
                        _logger.LogInformation("Đã xóa file pid: {FilePath}", pidFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Không thể xóa file pid {FilePath}: {Error}", pidFile, ex.Message);
                    }
                }
                
                await Task.Delay(1000); // Đợi để đảm bảo các file đã được xóa
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dọn dẹp file tạm thời của SteamCMD");
            }
        }
        #endregion

        /// <summary>
        /// Lấy tên game từ AppID
        /// </summary>
        private async Task<string> GetGameNameFromAppId(string appId)
        {
            if (string.IsNullOrEmpty(appId))
                return "Unknown Game";
                
            try
            {
                // Thử lấy từ cache hay API
                var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                if (appInfo != null && !string.IsNullOrEmpty(appInfo.Name))
                {
                    return appInfo.Name;
                }
                
                // Thử lấy từ manifest
                string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
                string steamappsDir = Path.Combine(steamCmdDir, "steamapps");
                
                if (Directory.Exists(steamappsDir))
                {
                    var manifestData = await ReadAppManifest(steamappsDir, appId);
                    if (manifestData != null && manifestData.TryGetValue("name", out var nameValue) && !string.IsNullOrWhiteSpace(nameValue))
                    {
                        return nameValue;
                    }
                }
                
                // Nếu không tìm thấy thì trả về AppID
                return $"Game {appId}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể lấy tên game cho AppID {AppId}: {Error}", appId, ex.Message);
                return $"Game {appId}";
            }
        }

        /// <summary>
        /// Xóa cache thông tin cập nhật của một ứng dụng cụ thể
        /// </summary>
        public async Task<bool> InvalidateAppUpdateCache(string appId)
        {
            try
            {
                _logger.LogInformation("Xóa cache thông tin cập nhật cho App ID: {AppId}", appId);
                
                // Xóa cache bằng cách gọi đến SteamApiService
                await _steamApiService.ClearAppUpdateCache(appId);
                
                // Thông báo thành công
                await SafeSendLogAsync("System", "Info", $"Đã xóa cache thông tin cập nhật cho App ID: {appId}");
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa cache thông tin cập nhật cho App ID: {AppId}", appId);
                return false;
            }
        }
        private void LogAccountDetails(string username, string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                _logger.LogWarning("Tài khoản hoặc mật khẩu rỗng");
                return;
            }

            _logger.LogDebug("Thông tin tài khoản:");
            _logger.LogDebug("- Username: {0}", username);
            _logger.LogDebug("- Password length: {0}", password.Length);
            
            // Kiểm tra dạng Base64
            bool isBase64Pattern = Regex.IsMatch(password, @"^[a-zA-Z0-9\+/]*={0,2}$");
            _logger.LogDebug("- Password có dạng Base64: {0}", isBase64Pattern);
            
            // Kiểm tra độ dài điển hình của mật khẩu mã hóa
            bool isEncryptedLength = password.Length > 20 && password.Length % 4 == 0;
            _logger.LogDebug("- Password có độ dài giống mã hóa: {0} (length={1}, divisible by 4={2})", 
                isEncryptedLength, password.Length, password.Length % 4 == 0);
                
            // Kiểm tra các đặc điểm phổ biến của mật khẩu mã hóa
            bool containsSpecialChars = password.Contains('+') || password.Contains('/') || password.Contains('=');
            _logger.LogDebug("- Password chứa ký tự đặc biệt của Base64 (+/=): {0}", containsSpecialChars);
            
            // Kiểm tra cường độ entropy của mật khẩu (mật khẩu mã hóa thường có entropy cao)
            double entropy = CalculateStringEntropy(password);
            _logger.LogDebug("- Password entropy (measure of randomness): {0:F2} (>4.5 có thể là mã hóa)", entropy);
            
            // Kết luận về khả năng mật khẩu đang mã hóa
            bool isLikelyEncrypted = isBase64Pattern && isEncryptedLength && entropy > 4.5;
            _logger.LogDebug("- KẾT LUẬN: Password có khả năng vẫn bị mã hóa: {0}", isLikelyEncrypted);

            // Chỉ in 5 ký tự đầu để kiểm tra, không in toàn bộ mật khẩu
            if (password.Length > 5)
            {
                _logger.LogDebug("- 5 ký tự đầu của password: {0}...", password.Substring(0, 5));
            }
        }
        
        // Helper method to calculate entropy of a string (measure of randomness)
        private double CalculateStringEntropy(string input)
        {
            var charCount = new Dictionary<char, int>();
            foreach (char c in input)
            {
                if (charCount.ContainsKey(c))
                    charCount[c]++;
                else
                    charCount[c] = 1;
            }
            
            double entropy = 0;
            int length = input.Length;
            foreach (var count in charCount.Values)
            {
                double frequency = (double)count / length;
                entropy -= frequency * Math.Log(frequency, 2);
            }
            
            return entropy;
        }
    }
}
#endregion