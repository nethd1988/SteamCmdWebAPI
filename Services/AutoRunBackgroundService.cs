using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Services
{
    public class AutoRunBackgroundService : BackgroundService
    {
        private readonly ILogger<AutoRunBackgroundService> _logger;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly object _runLock = new object();

        private const int CheckIntervalMinutes = 1;
        private DateTime _lastAutoRunTime = DateTime.MinValue;

        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            ProfileService profileService,
            SteamCmdService steamCmdService)
        {
            _logger = logger;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoRunBackgroundService đã khởi động");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await CheckAndRunScheduledTasksAsync();
                    await Task.Delay(TimeSpan.FromMinutes(CheckIntervalMinutes), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi trong quá trình auto-run");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
        }

        private async Task CheckAndRunScheduledTasksAsync()
        {
            var settings = await _profileService.LoadAutoRunSettings();

            if (!settings.AutoRunEnabled) return;

            var now = DateTime.Now;
            var timeSinceLastRun = now - _lastAutoRunTime;

            if (_lastAutoRunTime == DateTime.MinValue ||
                timeSinceLastRun.TotalHours >= settings.AutoRunIntervalHours)
            {
                lock (_runLock)
                {
                    if (_lastAutoRunTime != DateTime.MinValue &&
                        now - _lastAutoRunTime < TimeSpan.FromHours(settings.AutoRunIntervalHours))
                    {
                        return;
                    }

                    _lastAutoRunTime = now;
                }

                try
                {
                    _logger.LogInformation("Bắt đầu chạy các profile tự động");
                    await _steamCmdService.StartAllAutoRunProfilesAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi chạy các profile tự động");
                }
            }
        }
    }
}