using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    // BỎ HOẶC COMMENT TOÀN BỘ CLASS NÀY
    //public class AutoRunService : BackgroundService
    //{
    //    private readonly ILogger<AutoRunService> _logger;
    //    private readonly ProfileService _profileService;
    //    private readonly SteamCmdService _steamCmdService;

    //    private TimeSpan _interval = TimeSpan.FromHours(12);
    //    private bool _enabled = true;
    //    public AutoRunService(
    //        ILogger<AutoRunService> logger,
    //        ProfileService profileService,
    //        SteamCmdService steamCmdService)
    //    {
    //        _logger = logger;
    //        _profileService = profileService;
    //        _steamCmdService = steamCmdService;
    //    }

    //    public void Configure(TimeSpan interval, bool enabled)
    //    {
    //        _interval = interval;
    //        _enabled = enabled;
    //    }

    //    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    //    {
    //        _logger.LogInformation("Auto Run Service đã khởi động");
    //        while (!stoppingToken.IsCancellationRequested)
    //        {
    //            try
    //            {
    //                if (_enabled)
    //                {
    //                    _logger.LogInformation("Đang chạy cập nhật tự động");
    //                    // Lấy tất cả profile
    //                    var profiles = await _profileService.GetAllProfiles();
    //                    foreach (var profile in profiles)
    //                    {
    //                        try
    //                        {
    //                            if (profile.AutoRun)
    //                            {
    //                                _logger.LogInformation($"Đang chạy tự động cập nhật cho profile '{profile.Name}'");
    //                                await _steamCmdService.RunProfileAsync(profile.Id);
    //                            }
    //                        }
    //                        catch (Exception ex)
    //                        {
    //                            _logger.LogError(ex, $"Lỗi khi chạy tự động profile '{profile.Name}'");
    //                        }
    //                    }
    //                }
    //                else
    //                {
    //                    _logger.LogDebug("Chế độ tự động đang bị tắt");
    //                }
    //            }
    //            catch (Exception ex)
    //            {
    //                _logger.LogError(ex, "Lỗi trong quá trình tự động chạy");
    //            }

    //            _logger.LogInformation($"Chờ {_interval.TotalHours} giờ cho lần chạy tự động tiếp theo");
    //            await Task.Delay(_interval, stoppingToken);
    //        }
    //    }
    //}
}