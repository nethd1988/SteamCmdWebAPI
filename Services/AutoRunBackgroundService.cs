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

        // Thời gian đồng bộ server (mỗi 30 phút)
        private readonly TimeSpan _serverSyncInterval = TimeSpan.FromMinutes(30);
        private DateTime _lastServerSync = DateTime.MinValue;

        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            SilentSyncService silentSyncService,
            ServerSettingsService serverSettingsService,
            ProfileService profileService)
        {
            _logger = logger;
            _silentSyncService = silentSyncService;
            _serverSettingsService = serverSettingsService;
            _profileService = profileService;
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
                    // Kiểm tra thời gian đồng bộ
                    if ((DateTime.Now - _lastServerSync) > _serverSyncInterval)
                    {
                        await TrySyncWithServerAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi trong quá trình đồng bộ tự động");
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
                    var (success, message) = await _silentSyncService.SyncAllProfilesAsync();

                    if (success)
                    {
                        _logger.LogInformation("Đồng bộ tự động thành công: {Message}", message);
                    }
                    else
                    {
                        _logger.LogWarning("Đồng bộ tự động không thành công: {Message}", message);
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
    }
}