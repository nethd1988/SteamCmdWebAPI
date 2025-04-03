using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public class AutoRunBackgroundService : BackgroundService
    {
        private readonly ILogger<AutoRunBackgroundService> _logger;
        private readonly SteamCmdService _steamCmdService;
        private readonly SettingsService _settingsService;
        private Timer _timer;

        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            SteamCmdService steamCmdService,
            SettingsService settingsService)
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
            _settingsService = settingsService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoRunBackgroundService is starting.");

            // Thiết lập timer để chạy theo lịch hẹn giờ
            _timer = new Timer(async _ =>
            {
                try
                {
                    var settings = await _settingsService.LoadSettingsAsync();
                    if (!settings.AutoRunEnabled) return;

                    // Lấy giờ hiện tại
                    var now = DateTime.Now;
                    int currentHour = now.Hour;

                    // Kiểm tra xem có phải giờ chạy theo lịch không
                    int scheduledHour = settings.ScheduledHour;
                    if (currentHour == scheduledHour && now.Minute == 0) // Chạy vào phút 0 của giờ
                    {
                        _logger.LogInformation("Bắt đầu chạy tất cả profile theo lịch hẹn giờ tại {Time}", now);
                        await _steamCmdService.RunAllProfilesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi chạy tự động theo lịch hẹn giờ");
                }
            }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1)); // Kiểm tra mỗi phút

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(60000, stoppingToken); // Chờ 1 phút
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("AutoRunBackgroundService is stopping.");
            _timer?.Dispose();
            await _steamCmdService.ShutdownAsync();
            await base.StopAsync(cancellationToken);
        }
    }
}