using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Services
{
    public class AutoRunBackgroundService : BackgroundService
    {
        private readonly ILogger<AutoRunBackgroundService> _logger;
        private readonly SilentSyncService _silentSyncService;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly ServerSyncService _serverSyncService; // Thêm trường này

        // Thời gian đồng bộ server (mỗi 30 phút)
        private readonly TimeSpan _serverSyncInterval = TimeSpan.FromMinutes(30);
        private DateTime _lastServerSync = DateTime.MinValue;
        private DateTime _lastAutoRunTime = DateTime.MinValue;

        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            SilentSyncService silentSyncService,
            ServerSettingsService serverSettingsService,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            ServerSyncService serverSyncService) // Thêm tham số này
        {
            _logger = logger;
            _silentSyncService = silentSyncService;
            _serverSettingsService = serverSettingsService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _serverSyncService = serverSyncService; // Khởi tạo giá trị
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoRunBackgroundService đã khởi động");

            // Đồng bộ khi khởi động
            await TrySyncWithServerAsync();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Kiểm tra thời gian đồng bộ server
                    if ((DateTime.Now - _lastServerSync) > _serverSyncInterval)
                    {
                        await TrySyncWithServerAsync();
                    }

                    // Kiểm tra và chạy auto-run theo cấu hình
                    await CheckAndRunScheduledTasksAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi trong quá trình đồng bộ hoặc auto-run");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Kiểm tra mỗi phút
            }
        }

        private async Task TrySyncWithServerAsync()
        {
            try
            {
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (serverSettings.EnableServerSync)
                {
                    _logger.LogInformation("Đang thực hiện đồng bộ tự động với server");

                    // Lấy danh sách profile từ server
                    var profileNames = await _serverSyncService.GetProfileNamesFromServerAsync();

                    if (profileNames.Count > 0)
                    {
                        _logger.LogInformation("Đã cập nhật danh sách profile từ server: {Count} profile", profileNames.Count);
                    }
                    else
                    {
                        _logger.LogWarning("Không tìm thấy profile nào trên server");
                    }

                    _lastServerSync = DateTime.Now;
                    await _serverSettingsService.UpdateLastSyncTimeAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thử đồng bộ với server");
            }
        }

        private async Task CheckAndRunScheduledTasksAsync()
        {
            try
            {
                var settings = await _profileService.LoadAutoRunSettings();
                if (!settings.AutoRunEnabled)
                {
                    return;
                }

                var now = DateTime.Now;
                // Kiểm tra dựa theo khoảng thời gian
                TimeSpan timeSinceLastRun = now - _lastAutoRunTime;
                int intervalHours = settings.AutoRunIntervalHours;

                if (_lastAutoRunTime == DateTime.MinValue || timeSinceLastRun.TotalHours >= intervalHours)
                {
                    _logger.LogInformation("Đang chạy tất cả profile tự động theo khoảng thời gian {0} giờ", intervalHours);
                    await _steamCmdService.StartAllAutoRunProfilesAsync();
                    _lastAutoRunTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra lịch hẹn auto-run");
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Đang dừng AutoRunBackgroundService...");
            await base.StopAsync(stoppingToken);
        }

        public override void Dispose()
        {
            _logger.LogInformation("Đang giải phóng tài nguyên AutoRunBackgroundService...");
            base.Dispose();
        }
    }
}