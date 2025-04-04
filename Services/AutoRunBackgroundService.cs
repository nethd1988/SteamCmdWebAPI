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
        private readonly SilentSyncService _silentSyncService;
        private Timer _timer;
        private readonly HttpClient _httpClient;

        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            SteamCmdService steamCmdService,
            SettingsService settingsService,
            ServerSettingsService serverSettingsService,
            SilentSyncService silentSyncService)
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
            _settingsService = settingsService;
            _serverSettingsService = serverSettingsService;
            _silentSyncService = silentSyncService;
            _httpClient = new HttpClient();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoRunBackgroundService is starting.");

            // Đảm bảo cài đặt server là idckz.ddnsfree.com
            await EnsureServerSettingsAsync();

            // Thực hiện đồng bộ ban đầu khi khởi động
            await PerformInitialSyncAsync();

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

        /// <summary>
        /// Đảm bảo cài đặt server là idckz.ddnsfree.com
        /// </summary>
        private async Task EnsureServerSettingsAsync()
        {
            try
            {
                _logger.LogInformation("Đảm bảo cài đặt server là idckz.ddnsfree.com...");

                var serverSettings = await _serverSettingsService.LoadSettingsAsync();

                // Luôn đặt địa chỉ server là idckz.ddnsfree.com
                serverSettings.ServerAddress = "idckz.ddnsfree.com";
                serverSettings.ServerPort = 61188;
                serverSettings.EnableServerSync = true;

                await _serverSettingsService.SaveSettingsAsync(serverSettings);

                _logger.LogInformation("Đã cấu hình kết nối đến server: {ServerAddress}:{Port}",
                    serverSettings.ServerAddress, serverSettings.ServerPort);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cấu hình cài đặt server");
            }
        }

        /// <summary>
        /// Thực hiện đồng bộ ban đầu khi khởi động service
        /// </summary>
        private async Task PerformInitialSyncAsync()
        {
            try
            {
                _logger.LogInformation("Thực hiện đồng bộ ban đầu với server khi khởi động...");

                // Kiểm tra cài đặt server
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();

                // Đảm bảo cài đặt đúng
                serverSettings.ServerAddress = "idckz.ddnsfree.com";
                serverSettings.ServerPort = 61188;
                serverSettings.EnableServerSync = true;

                await _serverSettingsService.SaveSettingsAsync(serverSettings);

                // Kiểm tra kết nối
                bool canConnect = await TestServerConnectionAsync(serverSettings.ServerAddress, serverSettings.ServerPort);
                if (!canConnect)
                {
                    _logger.LogWarning("Không thể kết nối đến server {ServerAddress}:{Port}, sẽ thử lại sau",
                        serverSettings.ServerAddress, serverSettings.ServerPort);
                    return;
                }

                // Thực hiện đồng bộ
                var (success, message) = await _silentSyncService.SyncAllProfilesAsync();
                if (success)
                {
                    _logger.LogInformation("Đồng bộ ban đầu thành công: {Message}", message);
                }
                else
                {
                    _logger.LogWarning("Đồng bộ ban đầu không thành công: {Message}", message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thực hiện đồng bộ ban đầu");
            }
        }

        /// <summary>
        /// Kiểm tra kết nối đến server
        /// </summary>
        private async Task<bool> TestServerConnectionAsync(string serverAddress, int port)
        {
            try
            {
                _logger.LogInformation("Kiểm tra kết nối đến server {ServerAddress}:{Port}", serverAddress, port);

                // Thử ping đến server
                string url = $"http://{serverAddress}:{port}/api/silentsync/status";
                var response = await _httpClient.GetAsync(url);

                bool success = response.IsSuccessStatusCode;
                _logger.LogInformation("Kết nối đến server {ServerAddress}:{Port} {Result}",
                    serverAddress, port, success ? "thành công" : "thất bại");

                // Cập nhật trạng thái kết nối
                await _serverSettingsService.UpdateConnectionStatusAsync(success ? "Connected" : "Disconnected");

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra kết nối đến server {ServerAddress}:{Port}", serverAddress, port);
                await _serverSettingsService.UpdateConnectionStatusAsync("Error");
                return false;
            }
        }

        private async Task SyncProfilesToServerAsync()
        {
            try
            {
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();

                // Đảm bảo cài đặt đúng
                serverSettings.ServerAddress = "idckz.ddnsfree.com";
                serverSettings.ServerPort = 61188;
                serverSettings.EnableServerSync = true;

                await _serverSettingsService.SaveSettingsAsync(serverSettings);

                _logger.LogInformation("Bắt đầu đồng bộ tự động với server {Server}:{Port}",
                    serverSettings.ServerAddress, serverSettings.ServerPort);

                // Sử dụng SilentSyncService để thực hiện đồng bộ
                var (success, message) = await _silentSyncService.SyncAllProfilesAsync();

                if (success)
                {
                    _logger.LogInformation("Đồng bộ profile thành công: {Message}", message);
                }
                else
                {
                    _logger.LogWarning("Đồng bộ profile không thành công: {Message}", message);
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
