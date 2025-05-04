using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public class LicenseValidationService : BackgroundService
    {
        private readonly ILogger<LicenseValidationService> _logger;
        private readonly LicenseService _licenseService;
        private readonly LicenseStateService _licenseStateService;
        private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6); // Kiểm tra mỗi 6 giờ

        public LicenseValidationService(
            ILogger<LicenseValidationService> logger, 
            LicenseService licenseService, 
            LicenseStateService licenseStateService)
        {
            _logger = logger;
            _licenseService = licenseService;
            _licenseStateService = licenseStateService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Dịch vụ kiểm tra license đã khởi động");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Đang kiểm tra tính hợp lệ của giấy phép...");
                    
                    var licenseResult = await _licenseService.ValidateLicenseAsync();

                    // Cập nhật trạng thái license
                    _licenseStateService.UpdateLicenseState(
                        licenseResult.IsValid, 
                        licenseResult.Message, 
                        licenseResult.UsingCache);

                    // Log kết quả kiểm tra
                    if (licenseResult.IsValid)
                    {
                        _logger.LogInformation("Kiểm tra giấy phép thành công: {Message}", licenseResult.Message);
                    }
                    else if (licenseResult.UsingCache)
                    {
                        _logger.LogWarning("Không thể kết nối đến máy chủ cấp phép, sử dụng cache: {Message}", licenseResult.Message);
                    }
                    else
                    {
                        _logger.LogError("Giấy phép không hợp lệ: {Message}", licenseResult.Message);
                    }

                    // Nếu đang dùng cache, giảm thời gian kiểm tra xuống 30 phút
                    TimeSpan delayTime = licenseResult.UsingCache ? 
                        TimeSpan.FromMinutes(30) : _checkInterval;

                    await Task.Delay(delayTime, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra giấy phép");
                    
                    // Nếu có lỗi, kiểm tra lại sau 30 phút
                    await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                }
            }
        }
    }
} 