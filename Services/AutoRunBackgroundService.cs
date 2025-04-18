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
        private readonly ServerSettingsService _serverSettingsService; // Thêm phần khai báo này
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
            ServerSyncService serverSyncService,
            ServerSettingsService serverSettingsService) // Thêm tham số này
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
            _settingsService = settingsService;
            _serverSyncService = serverSyncService;
            _serverSettingsService = serverSettingsService; // Thêm khởi tạo này
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoRunBackgroundService is starting.");

            // Chạy một lần khi khởi động nếu AutoRunEnabled = true
            try
            {
                var settings = await _settingsService.LoadSettingsAsync();
                if (settings.AutoRunEnabled)
                {
                    _logger.LogInformation("Bắt đầu chạy tất cả profile tự động khi khởi động");
                    await _steamCmdService.RunAllProfilesAsync();
                    _lastAutoRunTime = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tự động lần đầu");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Kiểm tra cài đặt tự động chạy
                    var settings = await _settingsService.LoadSettingsAsync();
                    if (settings.AutoRunEnabled)
                    {
                        var now = DateTime.Now;
                        int intervalHours = settings.AutoRunIntervalHours;
                        TimeSpan timeSinceLastRun = now - _lastAutoRunTime;

                        // Kiểm tra xem đã đến thời gian chạy tiếp theo chưa
                        if (_lastAutoRunTime == DateTime.MinValue || timeSinceLastRun.TotalHours >= intervalHours)
                        {
                            _logger.LogInformation("Bắt đầu chạy tất cả profile theo khoảng thời gian {Hours} giờ", intervalHours);
                            await _steamCmdService.RunAllProfilesAsync();
                            _lastAutoRunTime = now;
                        }
                    }

                    // Chỉ cập nhật danh sách profile từ server theo định kỳ, không tự động đồng bộ
                    if ((DateTime.Now - _lastServerSync) > _serverSyncInterval)
                    {
                        var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                        if (serverSettings.EnableServerSync)
                        {
                            _logger.LogInformation("Bắt đầu cập nhật danh sách profile từ server");
                            // Chỉ lấy danh sách profile từ server, không tự động đồng bộ
                            await _serverSyncService.GetProfileNamesFromServerAsync();
                            _lastServerSync = DateTime.Now;
                            _logger.LogInformation("Hoàn thành cập nhật danh sách profile từ server");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra tự động hoặc đồng bộ server");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Kiểm tra mỗi phút
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("AutoRunBackgroundService is stopping.");
            await _steamCmdService.ShutdownAsync();
            _httpClient.Dispose();
            await base.StopAsync(cancellationToken);
        }
    }
}