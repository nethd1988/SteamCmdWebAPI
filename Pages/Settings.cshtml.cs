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
        private readonly UpdateCheckService _updateCheckService; // Inject UpdateCheckService

        // Properties for Auto Run Settings
        [BindProperty] // Use BindProperty for form data
        public bool AutoRunEnabled { get; set; }
        [BindProperty] // Use BindProperty for form data
        public int AutoRunIntervalHours { get; set; } = 12; // Mặc định 12 giờ

        // Properties for Auto Update Check Settings
        [BindProperty] // Use BindProperty for form data
        public bool UpdateCheckEnabled { get; set; }
        [BindProperty] // Use BindProperty for form data
        public int UpdateCheckIntervalMinutes { get; set; } = 60; // Mặc định 60 phút (1 giờ)

        // *** ADD THIS PROPERTY ***
        [BindProperty] // Add BindProperty to receive data from the form
        public bool AutoUpdateProfiles { get; set; } // Define the missing property

        public SettingsPageModel(
            ILogger<SettingsPageModel> logger,
            IHubContext<LogHub> hubContext,
            SettingsService settingsService,
            UpdateCheckService updateCheckService) // Inject UpdateCheckService
        {
            _logger = logger;
            _hubContext = hubContext;
            _settingsService = settingsService;
            _updateCheckService = updateCheckService; // Assign injected service
        }

        public async Task OnGetAsync()
        {
            _logger.LogInformation("Đang tải cài đặt...");

            // Load Auto Run Settings
            var autoRunSettings = await _settingsService.LoadSettingsAsync();
            AutoRunEnabled = autoRunSettings.AutoRunEnabled;

            // Chuyển đổi từ cài đặt cũ sang mới nếu cần cho AutoRunIntervalHours
            if (autoRunSettings.AutoRunIntervalHours > 0)
            {
                AutoRunIntervalHours = autoRunSettings.AutoRunIntervalHours;
            }
            else
            {
                // Nếu dùng cài đặt cũ, chuyển đổi sang giờ
                switch (autoRunSettings.AutoRunInterval?.ToLower())
                {
                    case "daily":
                        AutoRunIntervalHours = 24;
                        break;
                    case "weekly":
                        AutoRunIntervalHours = 168; // 7 * 24
                        break;
                    case "monthly":
                        AutoRunIntervalHours = 720; // 30 * 24 (gần đúng)
                        break;
                    default:
                        AutoRunIntervalHours = 12; // Mặc định
                        break;
                }
            }
            // Giới hạn AutoRunIntervalHours trong khoảng 1-48 giờ
            if (AutoRunIntervalHours < 1) AutoRunIntervalHours = 1;
            if (AutoRunIntervalHours > 48) AutoRunIntervalHours = 48;


            // Load Update Check Settings
            var updateCheckSettings = _updateCheckService.GetCurrentSettings();
            UpdateCheckEnabled = updateCheckSettings.Enabled;
            UpdateCheckIntervalMinutes = updateCheckSettings.IntervalMinutes;
            // *** ALSO LOAD THE NEW PROPERTY ***
            AutoUpdateProfiles = updateCheckSettings.AutoUpdateProfiles; // Load the initial value for the checkbox

            // Giới hạn UpdateCheckIntervalMinutes trong khoảng hợp lý (ví dụ 10 phút đến 1440 phút = 24 giờ)
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

                // Use UpdateCheckService to update settings
                _updateCheckService.UpdateSettings(
                    UpdateCheckEnabled,
                    TimeSpan.FromMinutes(UpdateCheckIntervalMinutes),
                    AutoUpdateProfiles // Sử dụng AutoUpdateProfiles từ form (this property is now defined)
                );

                TempData["SuccessMessage"] = $"Cấu hình kiểm tra cập nhật tự động đã được cập nhật: {(UpdateCheckEnabled ? "Bật" : "Tắt")}, " +
                                            $"{UpdateCheckIntervalMinutes} phút/lần, Tự động cập nhật khi phát hiện: {(AutoUpdateProfiles ? "Bật" : "Tắt")}";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật tự động");
                TempData["ErrorMessage"] = $"Lỗi khi lưu cài đặt kiểm tra cập nhật tự động: {ex.Message}";
                return RedirectToPage();
            }
        }


        // Helper method để chuyển đổi giờ thành chuỗi tương thích ngược (có thể không cần nữa)
        // private string ConvertIntervalHoursToString(int hours)
        // {
        //     if (hours <= 24) return "daily";
        //     if (hours <= 168) return "weekly";
        //     return "monthly";
        // }
    }
}