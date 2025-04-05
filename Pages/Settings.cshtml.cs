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

        public bool AutoRunEnabled { get; set; }
        public int AutoRunIntervalHours { get; set; } = 12; // Mặc định 12 giờ
        public int ScheduledHour { get; set; } = 7;

        public SettingsPageModel(
            ILogger<SettingsPageModel> logger,
            IHubContext<LogHub> hubContext,
            SettingsService settingsService)
        {
            _logger = logger;
            _hubContext = hubContext;
            _settingsService = settingsService;
        }

        public async Task OnGetAsync()
        {
            _logger.LogInformation("Đang tải cài đặt tự động chạy...");
            var settings = await _settingsService.LoadSettingsAsync();
            AutoRunEnabled = settings.AutoRunEnabled;
            
            // Chuyển đổi từ cài đặt cũ sang mới nếu cần
            if (settings.AutoRunIntervalHours > 0)
            {
                AutoRunIntervalHours = settings.AutoRunIntervalHours;
            }
            else
            {
                // Nếu dùng cài đặt cũ, chuyển đổi sang giờ
                switch (settings.AutoRunInterval?.ToLower())
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
            
            // Giới hạn trong khoảng 1-48 giờ
            if (AutoRunIntervalHours < 1) AutoRunIntervalHours = 1;
            if (AutoRunIntervalHours > 48) AutoRunIntervalHours = 48;
            
            ScheduledHour = settings.ScheduledHour;
            _logger.LogInformation("Đã tải cài đặt tự động chạy");
        }

        public async Task<IActionResult> OnPostAutoRunAsync(bool autoRunEnabled, int autoRunInterval, int scheduledHour)
        {
            try
            {
                if (scheduledHour < 1 || scheduledHour > 24)
                {
                    return new JsonResult(new { success = false, error = "Giờ hẹn phải từ 1h đến 24h." }) { StatusCode = 400 };
                }
                
                if (autoRunInterval < 1 || autoRunInterval > 48)
                {
                    return new JsonResult(new { success = false, error = "Khoảng thời gian chạy phải từ 1 đến 48 giờ." }) { StatusCode = 400 };
                }

                var settings = new SteamCmdWebAPI.Models.AutoRunSettings
                {
                    AutoRunEnabled = autoRunEnabled,
                    AutoRunIntervalHours = autoRunInterval,
                    AutoRunInterval = ConvertIntervalHoursToString(autoRunInterval),
                    ScheduledHour = scheduledHour
                };

                await _settingsService.SaveSettingsAsync(settings);

                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cấu hình tự động chạy đã được cập nhật: {(autoRunEnabled ? "Bật" : "Tắt")}, {autoRunInterval} giờ/lần, bắt đầu lúc {scheduledHour}h");
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cấu hình tự động chạy");
                return new JsonResult(new { success = false, error = $"Lỗi khi lưu cấu hình tự động chạy: {ex.Message}" }) { StatusCode = 500 };
            }
        }
        
        // Helper method để chuyển đổi giờ thành chuỗi tương thích ngược
        private string ConvertIntervalHoursToString(int hours)
        {
            if (hours <= 24) return "daily";
            if (hours <= 168) return "weekly";
            return "monthly";
        }
    }
}