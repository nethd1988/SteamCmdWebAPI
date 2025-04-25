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
            // Giới hạn UpdateCheckIntervalMinutes trong khoảng hợp lý (ví dụ 10 phút đến 1440 phút = 24 giờ)
            if (UpdateCheckIntervalMinutes < 10) UpdateCheckIntervalMinutes = 10;
            if (UpdateCheckIntervalMinutes > 1440) UpdateCheckIntervalMinutes = 1440;


            _logger.LogInformation("Đã tải cài đặt");
        }

        // Handler to save Auto Run Settings
        public async Task<IActionResult> OnPostSaveAutoRunSettingsAsync() // Renamed handler
        {
            try
            {
                // Validation for AutoRunIntervalHours
                if (AutoRunIntervalHours < 1 || AutoRunIntervalHours > 48)
                {
                    TempData["ErrorMessage"] = "Khoảng thời gian chạy tự động (giờ) phải từ 1 đến 48.";
                    return RedirectToPage();
                }

                var settings = new SteamCmdWebAPI.Models.AutoRunSettings
                {
                    AutoRunEnabled = AutoRunEnabled, // Use BindProperty value
                    AutoRunIntervalHours = AutoRunIntervalHours, // Use BindProperty value
                    // ConvertIntervalHoursToString might not be needed anymore if AutoRunService uses AutoRunIntervalHours directly
                    // AutoRunInterval = ConvertIntervalHoursToString(AutoRunIntervalHours)
                };

                await _settingsService.SaveSettingsAsync(settings);

                TempData["SuccessMessage"] = $"Cấu hình tự động chạy đã được cập nhật: {(AutoRunEnabled ? "Bật" : "Tắt")}, {AutoRunIntervalHours} giờ/lần";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cấu hình tự động chạy");
                TempData["ErrorMessage"] = $"Lỗi khi lưu cấu hình tự động chạy: {ex.Message}";
                return RedirectToPage();
            }
        }

        // Handler to save Update Check Settings
        public async Task<IActionResult> OnPostSaveUpdateCheckSettingsAsync() // New handler
        {
            try
            {
                // Validation for UpdateCheckIntervalMinutes
                if (UpdateCheckIntervalMinutes < 10 || UpdateCheckIntervalMinutes > 1440) // Example range: 10 mins to 24 hours
                {
                    TempData["ErrorMessage"] = "Khoảng thời gian kiểm tra cập nhật (phút) phải từ 10 đến 1440.";
                    return RedirectToPage();
                }

                // Use UpdateCheckService to update settings
                _updateCheckService.UpdateSettings(
                    UpdateCheckEnabled, // Use BindProperty value
                    TimeSpan.FromMinutes(UpdateCheckIntervalMinutes), // Use BindProperty value
                    true // Assuming AutoUpdateProfiles should be linked to UpdateCheckEnabled or be a separate setting if needed
                         // For simplicity, linking AutoUpdateProfiles to UpdateCheckEnabled here.
                         // If you need a separate checkbox for "Auto-update profiles when update found",
                         // you'll need to add a property for it and pass that value.
                );

                TempData["SuccessMessage"] = $"Cấu hình kiểm tra cập nhật tự động đã được cập nhật: {(UpdateCheckEnabled ? "Bật" : "Tắt")}, {UpdateCheckIntervalMinutes} phút/lần";
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cấu hình kiểm tra cập nhật tự động");
                TempData["ErrorMessage"] = $"Lỗi khi lưu cấu hình kiểm tra cập nhật tự động: {ex.Message}";
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
