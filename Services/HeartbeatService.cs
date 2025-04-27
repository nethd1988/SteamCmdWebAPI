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
        private readonly TimeSpan _interval = TimeSpan.FromMinutes(10);

        public HeartbeatService(
            ILogger<HeartbeatService> logger,
            TcpClientService tcpClientService)
        {
            _logger = logger;
            _tcpClientService = tcpClientService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Dịch vụ Heartbeat đã khởi động. Sẽ kiểm tra kết nối mỗi {Minutes} phút", _interval.TotalMinutes);

            // Chờ một chút để hệ thống khởi động đầy đủ
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _tcpClientService.PeriodicHeartbeatAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi thực hiện heartbeat");
                }

                await Task.Delay(_interval, stoppingToken);
            }

            _logger.LogInformation("Dịch vụ Heartbeat đã dừng");
        }
    }
}