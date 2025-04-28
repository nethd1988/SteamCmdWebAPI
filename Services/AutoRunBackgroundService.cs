using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Linq; // Ensure Linq is included if needed by other parts not shown
using System.Threading;
using System.Threading.Tasks;
// Assuming these services exist in this namespace based on the original file
using SteamCmdWebAPI.Services;

// Namespace from original .cs file
namespace SteamCmdWebAPI.Services
{
    public class AutoRunBackgroundService : BackgroundService
    {
        private readonly ILogger<AutoRunBackgroundService> _logger;
        // Assuming these service definitions are correct from the original .cs file
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly TcpClientService _tcpClientService;

        // Field added from 1.txt
        private readonly string _clientId;

        // Original fields from .cs file
        // Thời gian kiểm tra server (mỗi 30 phút) - This seems unused in the provided methods, but retained
        private readonly TimeSpan _serverSyncInterval = TimeSpan.FromMinutes(30);

        // Theo dõi thời gian chạy tự động gần nhất
        private DateTime _lastAutoRunTime = DateTime.MinValue;
        private volatile bool _isRunningAutoTask = false;

        // Updated Constructor from 1.txt and original .cs file
        public AutoRunBackgroundService(
            ILogger<AutoRunBackgroundService> logger,
            ServerSettingsService serverSettingsService, // Assuming this exists
            ProfileService profileService,
            SteamCmdService steamCmdService,
            TcpClientService tcpClientService) // Assuming this exists
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService; // Semicolon added
            _profileService = profileService; // Semicolon added
            _steamCmdService = steamCmdService; // Semicolon added
            _tcpClientService = tcpClientService; // Semicolon added

            // Create ClientID when initializing the service (from 1.txt)
            _clientId = GetClientIdentifier(); // Semicolon added
            _logger.LogInformation("AutoRunBackgroundService initialized with ClientID: {ClientId}", _clientId); // Semicolon added
        }

        // New method added from 1.txt
        private string GetClientIdentifier()
        {
            try
            {
                string machineName = Environment.MachineName; // Semicolon added
                string userName = Environment.UserName; // Semicolon added
                // Combine machine name, username, and date for a somewhat unique ID per day
                string clientId = $"{machineName}-{userName}-{DateTime.Now:yyyyMMdd}"; // Semicolon added
                return clientId; // Semicolon added
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate client identifier. Using default."); // Semicolon added
                                                                                              // Fallback identifier in case of error reading environment variables
                return $"unknown-{Guid.NewGuid().ToString().Substring(0, 8)}"; // Semicolon added
            }
        }


        // Updated ExecuteAsync from 1.txt and original .cs file
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Log including ClientID (from 1.txt)
            _logger.LogInformation("AutoRunBackgroundService đã khởi động với ClientID: {ClientId}", _clientId); // Semicolon added

            // Initial delay from original .cs file
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Semicolon added

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!_isRunningAutoTask)
                {
                    try
                    {
                        // Log ClientID when running the task (from 1.txt)
                        _logger.LogInformation("Running auto task check with ClientID: {ClientId}", _clientId); // Semicolon added

                        // Check and run auto-run according to configuration (original .cs logic)
                        await CheckAndRunScheduledTasksAsync(); // Semicolon added
                    }
                    catch (Exception ex)
                    {
                        // Log error including ClientID (from 1.txt)
                        _logger.LogError(ex, "Lỗi trong quá trình auto-run cho ClientID: {ClientId}", _clientId); // Semicolon added
                    }
                }
                else
                {
                    // Optional: Log if the task is skipped because it's already running
                    _logger.LogDebug("Auto run task skipped, already running for ClientID: {ClientId}", _clientId); // Semicolon added
                }

                // Delay from original .cs file
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Semicolon added
            }
            _logger.LogInformation("AutoRunBackgroundService is stopping for ClientID: {ClientId}", _clientId); // Semicolon added
        }

        // CheckAndRunScheduledTasksAsync from original .cs file (unchanged by 1.txt logic but semicolons added)
        // Trong Services/AutoRunBackgroundService.cs, sửa đổi phương thức CheckAndRunScheduledTasksAsync() để đảm bảo chạy tất cả app khi tự động chạy theo lịch trình

        private async Task CheckAndRunScheduledTasksAsync()
        {
            if (_isRunningAutoTask)
                return;

            try
            {
                // Lấy cài đặt tự động chạy
                var settings = await _profileService.LoadAutoRunSettings();
                if (settings == null || !settings.AutoRunEnabled)
                {
                    return;
                }

                var now = DateTime.Now;
                TimeSpan timeSinceLastRun = now - _lastAutoRunTime;
                int intervalHours = settings.AutoRunIntervalHours;

                if (intervalHours <= 0)
                {
                    return;
                }

                if (_lastAutoRunTime == DateTime.MinValue || timeSinceLastRun.TotalHours >= intervalHours)
                {
                    _isRunningAutoTask = true;
                    try
                    {
                        _logger.LogInformation("AutoRun triggered by schedule for ClientID {ClientId}. Starting all marked profiles...", _clientId);

                        // Đảm bảo rằng khi chạy theo lịch trình, cũng sẽ chạy tất cả apps (chính và phụ thuộc)
                        // bằng cách gọi StartAllAutoRunProfilesAsync thay vì chạy từng profile một
                        await _steamCmdService.StartAllAutoRunProfilesAsync();

                        _lastAutoRunTime = now;
                        _logger.LogInformation("AutoRun task completed for ClientID {ClientId}. Next run check after {IntervalHours} hours.", _clientId, intervalHours);
                    }
                    catch (Exception runEx)
                    {
                        _logger.LogError(runEx, "Error occurred while running AutoRun profiles for ClientID {ClientId}", _clientId);
                    }
                    finally
                    {
                        _isRunningAutoTask = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra lịch hẹn auto-run cho ClientID {ClientId}", _clientId);
                _isRunningAutoTask = false;
            }
        }

        // StopAsync from original .cs file (semicolons added)
        public override async Task StopAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Đang dừng AutoRunBackgroundService cho ClientID {ClientId}...", _clientId); // Semicolon added
            await base.StopAsync(stoppingToken); // Semicolon added
            _logger.LogInformation("AutoRunBackgroundService đã dừng cho ClientID {ClientId}.", _clientId); // Semicolon added
        }
    }
}