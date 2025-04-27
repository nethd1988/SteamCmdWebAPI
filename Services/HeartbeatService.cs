// SteamCmdWebAPI/Services/HeartbeatService.cs
using Microsoft.Extensions.DependencyInjection; // Không cần nếu inject trực tiếp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public class HeartbeatService : BackgroundService
    {
        private readonly ILogger<HeartbeatService> _logger;
        private readonly TcpClientService _tcpClientService;
        private readonly TimeSpan _initialDelay = TimeSpan.FromSeconds(30); // Đợi lâu hơn để service khởi động ổn định
        private readonly TimeSpan _heartbeatInterval = TimeSpan.FromMinutes(3); // Gửi thường xuyên hơn để duy trì kết nối
        private int _failedAttempts = 0;
        private const int MAX_FAILED_ATTEMPTS = 3;

        public HeartbeatService(
            ILogger<HeartbeatService> logger,
            TcpClientService tcpClientService)
        {
            _logger = logger;
            _tcpClientService = tcpClientService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("HeartbeatService starting.");

            try
            {
                // Chờ một khoảng thời gian ngắn trước khi bắt đầu vòng lặp chính
                // Để đảm bảo các service khác (như TcpClientService lấy ClientID) đã khởi tạo xong
                _logger.LogInformation("HeartbeatService initial delay for {InitialDelaySeconds} seconds...", _initialDelay.TotalSeconds);
                await Task.Delay(_initialDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("HeartbeatService initial delay canceled. Service stopping.");
                return; // Service bị dừng trước khi bắt đầu vòng lặp
            }

            _logger.LogInformation("HeartbeatService entering main loop.");

            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("HeartbeatService loop executing at: {time}", DateTimeOffset.Now);
                try
                {
                    // Sử dụng instance TcpClientService đã được inject
                    _logger.LogInformation("HeartbeatService: Calling PeriodicHeartbeatAsync...");
                    await _tcpClientService.PeriodicHeartbeatAsync(stoppingToken);
                    _logger.LogInformation("HeartbeatService: PeriodicHeartbeatAsync call completed.");
                }
                catch (Exception ex)
                {
                    // Log lỗi xảy ra khi gọi PeriodicHeartbeatAsync nhưng không dừng vòng lặp
                    _logger.LogError(ex, "An error occurred while calling PeriodicHeartbeatAsync in HeartbeatService loop.");
                    // Có thể thêm các xử lý lỗi khác ở đây nếu cần
                }

                try
                {
                    // Chờ khoảng thời gian đã định trước khi gửi heartbeat tiếp theo
                    _logger.LogInformation("HeartbeatService waiting for {HeartbeatIntervalMinutes} minutes...", _heartbeatInterval.TotalMinutes);
                    await Task.Delay(_heartbeatInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Xử lý khi Task.Delay bị hủy (do stoppingToken) -> Service đang dừng
                    _logger.LogInformation("HeartbeatService delay was canceled. Service stopping.");
                    break; // Thoát khỏi vòng lặp while
                }
                catch (Exception ex)
                {
                    // Log các lỗi không mong muốn khác từ Task.Delay (ít khi xảy ra)
                    _logger.LogError(ex, "An unexpected error occurred during Task.Delay in HeartbeatService.");
                    // Cân nhắc có nên break vòng lặp ở đây không tùy thuộc vào yêu cầu
                }
            }

            _logger.LogInformation("HeartbeatService main loop finished. Service stopping.");
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("HeartbeatService stopping...");
            await base.StopAsync(cancellationToken);
            _logger.LogInformation("HeartbeatService stopped.");
        }
    }
}