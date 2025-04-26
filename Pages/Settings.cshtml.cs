using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class SettingsPageModel : PageModel
    {
        private readonly ILogger<SettingsPageModel> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly SettingsService _settingsService;
        private readonly UpdateCheckService _updateCheckService;

        // Properties for Auto Run Settings - Đặt mặc định theo hình
        [BindProperty]
        public bool AutoRunEnabled { get; set; } = true; // Mặc định bật

        [BindProperty]
        public int AutoRunIntervalHours { get; set; } = 1; // Mặc định 1 tiếng

        // Properties for Auto Update Check Settings - Đặt mặc định theo hình
        [BindProperty]
        public bool UpdateCheckEnabled { get; set; } = true; // Mặc định bật

        [BindProperty]
        public int UpdateCheckIntervalMinutes { get; set; } = 10; // Mặc định 10 phút

        [BindProperty]
        public bool AutoUpdateProfiles { get; set; } = true; // Mặc định bật

        public SettingsPageModel(
            ILogger<SettingsPageModel> logger,
            IHubContext<LogHub> hubContext,
            SettingsService settingsService,
            UpdateCheckService updateCheckService = null) // Thêm giá trị mặc định null
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _updateCheckService = updateCheckService; // Có thể null
        }

        public async Task OnGetAsync()
        {
            _logger.LogInformation("Đang tải cài đặt...");

            // Load Auto Run Settings
            var autoRunSettings = await _settingsService.LoadSettingsAsync();

            // Nếu không có cài đặt, sử dụng giá trị mặc định mới
            AutoRunEnabled = autoRunSettings?.AutoRunEnabled ?? true;
            AutoRunIntervalHours = autoRunSettings?.AutoRunIntervalHours > 0
                ? autoRunSettings.AutoRunIntervalHours
                : 1; // Mặc định 1 tiếng nếu không có giá trị

            // Giới hạn trong khoảng hợp lý
            if (AutoRunIntervalHours < 1) AutoRunIntervalHours = 1;
            if (AutoRunIntervalHours > 48) AutoRunIntervalHours = 48;

            // Load Update Check Settings - Kiểm tra null
            if (_updateCheckService != null)
            {
                var updateCheckSettings = _updateCheckService.GetCurrentSettings();
                UpdateCheckEnabled = updateCheckSettings?.Enabled ?? true;
                UpdateCheckIntervalMinutes = updateCheckSettings?.IntervalMinutes > 0
                    ? updateCheckSettings.IntervalMinutes
                    : 10; // Mặc định 10 phút
                AutoUpdateProfiles = updateCheckSettings?.AutoUpdateProfiles ?? true;
            }
            else
            {
                // Giá trị mặc định khi không có dịch vụ
                UpdateCheckEnabled = true;
                UpdateCheckIntervalMinutes = 10;
                AutoUpdateProfiles = true;
            }

            // Giới hạn UpdateCheckIntervalMinutes trong khoảng hợp lý
            if (UpdateCheckIntervalMinutes < 10) UpdateCheckIntervalMinutes = 10;
            if (UpdateCheckIntervalMinutes > 1440) UpdateCheckIntervalMinutes = 1440;

            _logger.LogInformation("Đã tải cài đặt");
        }

        // Handler to save Auto Run Settings
        public async Task<IActionResult> OnPostSaveAutoRunSettingsAsync()
        {
            _logger.LogInformation("SettingsPageModel: Handler OnPostSaveAutoRunSettingsAsync được kích hoạt.");
            _logger.LogInformation("SettingsPageModel: Nhận giá trị từ form - AutoRunEnabled: {AutoRunEnabled}, AutoRunIntervalHours: {AutoRunIntervalHours}",
                                   AutoRunEnabled, AutoRunIntervalHours);

            try
            {
                // Validation for AutoRunIntervalHours
                if (AutoRunIntervalHours < 1 || AutoRunIntervalHours > 48)
                {
                    TempData["ErrorMessage"] = "Khoảng thời gian chạy tự động (giờ) phải từ 1 đến 48.";
                    _logger.LogWarning("SettingsPageModel: Lỗi validation khoảng thời gian chạy tự động. Giá trị nhận được: {AutoRunIntervalHours}", AutoRunIntervalHours);
                    return RedirectToPage();
                }

                var settings = new SteamCmdWebAPI.Models.AutoRunSettings
                {
                    AutoRunEnabled = AutoRunEnabled, // Use BindProperty value
                    AutoRunIntervalHours = AutoRunIntervalHours, // Use BindProperty value
                };

                _logger.LogInformation("SettingsPageModel: Gọi SettingsService.SaveSettingsAsync...");
                await _settingsService.SaveSettingsAsync(settings); // Call to save
                _logger.LogInformation("SettingsPageModel: SettingsService.SaveSettingsAsync hoàn thành.");


                TempData["SuccessMessage"] = $"Cấu hình tự động chạy đã được cập nhật: {(AutoRunEnabled ? "Bật" : "Tắt")}, {AutoRunIntervalHours} giờ/lần";
                _logger.LogInformation("SettingsPageModel: Lưu cấu hình tự động chạy thành công.");
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SettingsPageModel: Lỗi khi lưu cấu hình tự động chạy");
                TempData["ErrorMessage"] = $"Lỗi khi lưu cấu hình tự động chạy: {ex.Message}";
                return RedirectToPage();
            }
        }

        // Handler to save Update Check Settings
        public async Task<IActionResult> OnPostSaveUpdateCheckSettingsAsync()
        {
            try
            {
                // Validation for UpdateCheckIntervalMinutes
                if (UpdateCheckIntervalMinutes < 10 || UpdateCheckIntervalMinutes > 1440)
                {
                    TempData["ErrorMessage"] = "Khoảng thời gian kiểm tra cập nhật (phút) phải từ 10 đến 1440.";
                    return RedirectToPage();
                }

                // Kiểm tra null trước khi sử dụng UpdateCheckService
                if (_updateCheckService != null)
                {
                    // Cập nhật cài đặt qua UpdateCheckService
                    _updateCheckService.UpdateSettings(
                        UpdateCheckEnabled,
                        TimeSpan.FromMinutes(UpdateCheckIntervalMinutes),
                        AutoUpdateProfiles
                    );

                    TempData["SuccessMessage"] = $"Cấu hình kiểm tra cập nhật tự động đã được cập nhật: {(UpdateCheckEnabled ? "Bật" : "Tắt")}, " +
                                            $"{UpdateCheckIntervalMinutes} phút/lần, Tự động cập nhật khi phát hiện: {(AutoUpdateProfiles ? "Bật" : "Tắt")}";
                }
                else
                {
                    _logger.LogWarning("Không thể lưu cài đặt kiểm tra cập nhật: Dịch vụ UpdateCheckService không khả dụng");
                    TempData["SuccessMessage"] = "Cài đặt đã được lưu nhưng dịch vụ kiểm tra cập nhật không khả dụng.";
                }

                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật tự động");
                TempData["ErrorMessage"] = $"Lỗi khi lưu cài đặt kiểm tra cập nhật tự động: {ex.Message}";
                return RedirectToPage();
            }
        }
    }
}