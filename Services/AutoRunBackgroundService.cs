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
        private readonly ServerSettingsService _serverSettingsService;
        private Timer _timer;
        private readonly HttpClient _httpClient;

        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            SteamCmdService steamCmdService,
            SettingsService settingsService,
            ServerSettingsService serverSettingsService)
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
            _settingsService = settingsService;
            _serverSettingsService = serverSettingsService;
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

                        // Thực hiện đồng bộ sau khi chạy các profile
                        await SyncProfilesToServerAsync();
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

        private async Task SyncProfilesToServerAsync()
        {
            try
            {
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (!serverSettings.EnableServerSync || string.IsNullOrEmpty(serverSettings.ServerAddress))
                {
                    _logger.LogInformation("Đồng bộ với server chưa được bật hoặc chưa cấu hình");
                    return;
                }

                _logger.LogInformation("Bắt đầu đồng bộ tự động với server {Server}:{Port}",
                    serverSettings.ServerAddress, serverSettings.ServerPort);

                string url = $"http://localhost:5000/api/appprofiles/sync?targetServer={serverSettings.ServerAddress}&port={serverSettings.ServerPort}";

                try
                {
                    var response = await _httpClient.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadAsStringAsync();
                        _logger.LogInformation("Đồng bộ profile thành công: {Result}", result);

                        // Cập nhật thời gian đồng bộ lần cuối
                        await _serverSettingsService.UpdateLastSyncTimeAsync();
                    }
                    else
                    {
                        var error = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning("Đồng bộ profile không thành công. HTTP Status: {Status}, Error: {Error}",
                            response.StatusCode, error);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi gọi API đồng bộ profile");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profile với server");
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