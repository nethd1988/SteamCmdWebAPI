using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Services
{
    public class AutoRunBackgroundService : BackgroundService
    {
        private readonly ILogger<AutoRunBackgroundService> _logger;
        private readonly SteamCmdService _steamCmdService;
        private readonly SettingsService _settingsService;
        private readonly ServerSyncService _serverSyncService;
        private Timer _timer;
        private readonly HttpClient _httpClient;

        // Thời gian đồng bộ server (mỗi 30 phút)
        private readonly TimeSpan _serverSyncInterval = TimeSpan.FromMinutes(30);
        private DateTime _lastServerSync = DateTime.MinValue;
        
        // Biến để theo dõi lần chạy cuối theo khoảng thời gian
        private DateTime _lastAutoRunTime = DateTime.MinValue;

        public AutoRunBackgroundService(
    ILogger<AutoRunBackgroundService> logger,
    SteamCmdService steamCmdService,
    SettingsService settingsService,
    ServerSyncService serverSyncService)
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
            _settingsService = settingsService;
            _serverSyncService = serverSyncService;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoRunBackgroundService is starting.");

            // Thiết lập timer để chạy theo lịch hẹn giờ
            _timer = new Timer(async _ =>
            {
                try
                {
                    // Kiểm tra cài đặt tự động chạy
                    var settings = await _settingsService.LoadSettingsAsync();
                    if (!settings.AutoRunEnabled) return;

                    // Lấy giờ hiện tại
                    var now = DateTime.Now;
                    int currentHour = now.Hour;
                    int currentMinute = now.Minute;

                    // Kiểm tra xem có phải giờ chạy theo lịch không (lần đầu tiên trong ngày)
                    int scheduledHour = settings.ScheduledHour;
                    if (currentHour == scheduledHour && currentMinute == 0) // Chạy vào phút 0 của giờ
                    {
                        _logger.LogInformation("Bắt đầu chạy tất cả profile theo lịch hẹn giờ tại {Time}", now);
                        await _steamCmdService.RunAllProfilesAsync();
                        _lastAutoRunTime = now;
                    }
                    
                    // Kiểm tra chạy theo khoảng thời gian
                    int intervalHours = settings.AutoRunIntervalHours;
                    TimeSpan timeSinceLastRun = now - _lastAutoRunTime;
                    
                    // Nếu đã qua khoảng thời gian cấu hình và không phải là lần chạy đầu
                    if (_lastAutoRunTime != DateTime.MinValue && 
                        timeSinceLastRun.TotalHours >= intervalHours)
                    {
                        _logger.LogInformation("Bắt đầu chạy tất cả profile theo khoảng thời gian {Hours} giờ", intervalHours);
                        await _steamCmdService.RunAllProfilesAsync();
                        _lastAutoRunTime = now;
                    }

                    // Đồng bộ tự động với server theo định kỳ
                    if ((DateTime.Now - _lastServerSync) > _serverSyncInterval)
                    {
                        _logger.LogInformation("Bắt đầu đồng bộ tự động với server");
                        await _serverSyncService.AutoSyncWithServerAsync();
                        _lastServerSync = DateTime.Now;
                        _logger.LogInformation("Hoàn thành đồng bộ tự động với server");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi chạy tự động theo lịch hẹn giờ hoặc đồng bộ server");
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
            _httpClient.Dispose();
            await base.StopAsync(cancellationToken);
        }
    }
}