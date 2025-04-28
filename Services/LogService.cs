using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Collections.Generic;

namespace SteamCmdWebAPI.Services
{
    public class LogService
    {
        private readonly string _logDirectory;
        private readonly string _currentLogFile;
        private readonly ConcurrentQueue<LogEntry> _logQueue;
        private readonly ILogger<LogService> _logger;
        private readonly int _maxLogFiles = 30; // Số ngày lưu log
        private readonly int _maxLogSize = 10 * 1024 * 1024; // 10MB
        private readonly object _lockObject = new object();
        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private const int MaxLogEntries = 5000;

        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Level { get; set; }
            public string ProfileName { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }
            public string ColorClass { get; set; }

            public LogEntry()
            {
                Timestamp = DateTime.Now;
            }
        }

        public LogService(ILogger<LogService> logger)
        {
            _logger = logger;
            _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            _currentLogFile = Path.Combine(_logDirectory, $"app_{DateTime.Now:yyyy-MM-dd}.log");
            _logQueue = new ConcurrentQueue<LogEntry>();

            // Tạo thư mục logs nếu chưa tồn tại
            if (!Directory.Exists(_logDirectory))
            {
                Directory.CreateDirectory(_logDirectory);
            }

            // Xóa các file log cũ
            CleanupOldLogs();

            // Bắt đầu xử lý queue
            Task.Run(ProcessLogQueue);
        }

        public void AddLog(string level, string message, string profileName = "", string status = "")
        {
            var entry = new LogEntry
            {
                Level = level,
                Message = message,
                ProfileName = string.IsNullOrWhiteSpace(profileName) ? "System" : profileName,
                Status = status,
                ColorClass = GetColorClassForStatus(status)
            };

            _logQueue.Enqueue(entry);

            lock (_lockObject)
            {
                _logs.Add(entry);
                if (_logs.Count > MaxLogEntries)
                {
                    _logs.RemoveRange(0, _logs.Count - MaxLogEntries);
                }
            }
        }

        private async Task ProcessLogQueue()
        {
            while (true)
            {
                try
                {
                    if (_logQueue.TryDequeue(out LogEntry logEntry))
                    {
                        await WriteLogToFile(logEntry);
                    }
                    else
                    {
                        await Task.Delay(100); // Đợi 100ms nếu không có log mới
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi xử lý log queue");
                }
            }
        }

        private async Task WriteLogToFile(LogEntry logEntry)
        {
            try
            {
                var logMessage = $"{logEntry.Timestamp:yyyy-MM-dd HH:mm:ss} [{logEntry.Level}] " +
                               $"[{logEntry.ProfileName}] [{logEntry.Status}] {logEntry.Message}";

                // Kiểm tra kích thước file
                var fileInfo = new FileInfo(_currentLogFile);
                if (fileInfo.Exists && fileInfo.Length > _maxLogSize)
                {
                    // Tạo file mới nếu file hiện tại quá lớn
                    var newFileName = Path.Combine(_logDirectory, $"app_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                    File.Move(_currentLogFile, newFileName);
                }

                // Ghi log vào file
                await File.AppendAllTextAsync(_currentLogFile, logMessage + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi ghi log vào file");
            }
        }

        private void CleanupOldLogs()
        {
            try
            {
                var logFiles = Directory.GetFiles(_logDirectory, "app_*.log")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                // Xóa các file log cũ hơn 30 ngày
                foreach (var file in logFiles.Skip(_maxLogFiles))
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Không thể xóa file log cũ: {file.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dọn dẹp log cũ");
            }
        }

        public List<LogEntry> GetLogs()
        {
            lock (_lockObject)
            {
                return _logs.OrderByDescending(l => l.Timestamp).ToList();
            }
        }

        public List<LogEntry> GetLogs(int page, int pageSize)
        {
            lock (_lockObject)
            {
                var orderedLogs = _logs.OrderByDescending(l => l.Timestamp).ToList();
                int skip = (page - 1) * pageSize;
                return orderedLogs.Skip(skip).Take(pageSize).ToList();
            }
        }

        public int GetTotalLogsCount()
        {
            lock (_lockObject)
            {
                return _logs.Count;
            }
        }

        public void ClearLogs()
        {
            lock (_lockObject)
            {
                _logs.Clear();
            }
        }

        private bool TryParseLogLine(string line, out LogEntry logEntry)
        {
            logEntry = null;
            try
            {
                // Format: 2024-03-21 10:30:45 [INFO] [ProfileName] [Status] Message
                var parts = line.Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    var timestampPart = parts[0].Trim();
                    if (DateTime.TryParse(timestampPart, out DateTime timestamp))
                    {
                        var level = parts[1].Trim();
                        var profileName = parts[2].Trim();
                        var status = parts[3].Trim();
                        var message = string.Join(" ", parts.Skip(4)).Trim();

                        logEntry = new LogEntry
                        {
                            Timestamp = timestamp,
                            Level = level,
                            ProfileName = profileName,
                            Status = status,
                            Message = message,
                            ColorClass = GetColorClassForStatus(status)
                        };
                        return true;
                    }
                }
            }
            catch
            {
                // Bỏ qua các dòng không đúng định dạng
            }
            return false;
        }

        private string GetColorClassForStatus(string status)
        {
            return status?.ToLower() switch
            {
                "success" => "text-success",
                "error" => "text-danger",
                "warning" => "text-warning",
                _ => "text-info"
            };
        }

        public void LoadLogsFromFiles()
        {
            try
            {
                lock (_lockObject)
                {
                    _logs.Clear();
                    var logFiles = Directory.GetFiles(_logDirectory, "app_*.log")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.CreationTime)
                        .ToList();

                    foreach (var file in logFiles)
                    {
                        if (File.Exists(file.FullName))
                        {
                            var lines = File.ReadAllLines(file.FullName);
                            foreach (var line in lines)
                            {
                                if (TryParseLogLine(line, out LogEntry logEntry))
                                {
                                    _logs.Add(logEntry);
                                    if (_logs.Count >= MaxLogEntries)
                                        break;
                                }
                            }
                            if (_logs.Count >= MaxLogEntries)
                                break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải logs từ files");
            }
        }
    }
}