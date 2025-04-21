using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SteamCmdWebAPI.Services
{
    public class LogFileReader
    {
        private readonly ILogger<LogFileReader> _logger;
        private string _logFilePath;
        private long _currentPosition = 0;
        private CancellationTokenSource _cts;
        private Action<string> _onNewLogContent;
        private bool _isRunning = false;

        public LogFileReader(ILogger<LogFileReader> logger)
        {
            _logger = logger;
            _cts = new CancellationTokenSource();
        }

        public void StartMonitoring(string logFilePath, Action<string> onNewLogContent)
        {
            if (_isRunning)
            {
                StopMonitoring();
            }

            _logFilePath = logFilePath;
            _onNewLogContent = onNewLogContent;
            _currentPosition = 0;
            _cts = new CancellationTokenSource();
            _isRunning = true;

            Task.Run(MonitorFileAsync, _cts.Token);
        }

        public void StopMonitoring()
        {
            if (!_isRunning) return;

            _cts.Cancel();
            _isRunning = false;
            _logger.LogInformation("Dừng theo dõi file log: {LogFile}", _logFilePath);
        }

        private async Task MonitorFileAsync()
        {
            try
            {
                _logger.LogInformation("Bắt đầu theo dõi file log: {LogFile}", _logFilePath);

                // Đợi file tồn tại
                while (!File.Exists(_logFilePath))
                {
                    if (_cts.Token.IsCancellationRequested) return;
                    await Task.Delay(500, _cts.Token);
                }

                // Lấy kích thước hiện tại của file
                var fileInfo = new FileInfo(_logFilePath);
                _currentPosition = fileInfo.Length;

                while (!_cts.Token.IsCancellationRequested)
                {
                    if (!File.Exists(_logFilePath))
                    {
                        await Task.Delay(500, _cts.Token);
                        continue;
                    }

                    try
                    {
                        string newContent = ReadNewContent();
                        if (!string.IsNullOrEmpty(newContent))
                        {
                            _onNewLogContent?.Invoke(newContent);
                        }
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Không thể đọc file log. Đang đợi...");
                    }

                    await Task.Delay(100, _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Người dùng đã cancel
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi theo dõi file log: {LogFile}", _logFilePath);
            }
            finally
            {
                _isRunning = false;
            }
        }

        private string ReadNewContent()
        {
            using (var fileStream = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fileStream.Length == _currentPosition)
                    return null;

                // Nếu file bị tạo lại hoặc bị cắt ngắn
                if (fileStream.Length < _currentPosition)
                    _currentPosition = 0;

                fileStream.Seek(_currentPosition, SeekOrigin.Begin);
                var bytesToRead = (int)(fileStream.Length - _currentPosition);
                var buffer = new byte[bytesToRead];
                var bytesRead = fileStream.Read(buffer, 0, bytesToRead);
                _currentPosition = fileStream.Position;

                // Chuyển đổi bytes thành chuỗi
                return Encoding.UTF8.GetString(buffer, 0, bytesRead);
            }
        }
    }
}