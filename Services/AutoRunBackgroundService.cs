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
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly TcpClientService _tcpClientService;

        // Thời gian kiểm tra server (mỗi 30 phút)
        private readonly TimeSpan _serverSyncInterval = TimeSpan.FromMinutes(30);

        // Theo dõi thời gian chạy tự động gần nhất
        private DateTime _lastAutoRunTime = DateTime.MinValue;
        private volatile bool _isRunningAutoTask = false;

        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            ServerSettingsService serverSettingsService,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            TcpClientService tcpClientService)
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _tcpClientService = tcpClientService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("AutoRunBackgroundService đã khởi động");

            // Đợi một chút trước khi bắt đầu
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_isRunningAutoTask)
                {
                    try
                    {
                        // Kiểm tra và chạy auto-run theo cấu hình
                        await CheckAndRunScheduledTasksAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi trong quá trình auto-run");
                    }
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Kiểm tra mỗi phút
            }
        }

        private async Task CheckAndRunScheduledTasksAsync()
        {
            if (_isRunningAutoTask)
                return;

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
                    _isRunningAutoTask = true;
                    try
                    {
                        _logger.LogInformation("AutoRun được bật, đang khởi động các profile được đánh dấu...");
                        await _steamCmdService.StartAllAutoRunProfilesAsync();
                        _lastAutoRunTime = now;
                    }
                    finally
                    {
                        _isRunningAutoTask = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra lịch hẹn auto-run");
                _isRunningAutoTask = false;
            }
        }

        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Đang dừng AutoRunBackgroundService...");
            await base.StopAsync(stoppingToken);
        }
    }
}