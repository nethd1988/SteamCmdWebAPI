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
    public class SettingsModel : PageModel
    {
        private readonly ILogger<SettingsModel> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly SettingsService _settingsService;

        public bool AutoRunEnabled { get; set; }
        public string AutoRunInterval { get; set; } = "daily";
        public int ScheduledHour { get; set; } = 7;

        public SettingsModel(
            ILogger<SettingsModel> logger,
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
            AutoRunInterval = settings.AutoRunInterval;
            ScheduledHour = settings.ScheduledHour;
            _logger.LogInformation("Đã tải cài đặt tự động chạy");
        }

        public async Task<IActionResult> OnPostAutoRunAsync(bool autoRunEnabled, string autoRunInterval, int scheduledHour)
        {
            try
            {
                if (scheduledHour < 1 || scheduledHour > 24)
                {
                    return new JsonResult(new { success = false, error = "Giờ hẹn phải từ 1h đến 24h." }) { StatusCode = 400 };
                }

                var settings = new AutoRunSettings
                {
                    AutoRunEnabled = autoRunEnabled,
                    AutoRunInterval = autoRunInterval,
                    ScheduledHour = scheduledHour
                };

                await _settingsService.SaveSettingsAsync(settings);

                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Cấu hình tự động chạy đã được cập nhật: {(autoRunEnabled ? "Bật" : "Tắt")}, {autoRunInterval}, {scheduledHour}h");
                return new JsonResult(new { success = true });
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cấu hình tự động chạy");
                return new JsonResult(new { success = false, error = $"Lỗi khi lưu cấu hình tự động chạy: {ex.Message}" }) { StatusCode = 500 };
            }
        }
    }
}