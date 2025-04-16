using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class LogsModel : PageModel
    {
        private readonly ILogger<LogsModel> _logger;
        private readonly SteamCmdService _steamCmdService;

        public List<SteamCmdService.LogEntry> Logs { get; set; } = new List<SteamCmdService.LogEntry>();

        public LogsModel(ILogger<LogsModel> logger, SteamCmdService steamCmdService)
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
        }

        public void OnGet()
        {
            _logger.LogInformation("Đang tải danh sách logs...");
            // Chỉ lấy các log có trạng thái "Success" hoặc "Error"
            Logs = _steamCmdService.GetLogs()
                .Where(l => l.Status == "Success" || l.Status == "Error")
                .ToList();
            _logger.LogInformation("Đã tải {0} logs", Logs.Count);
        }
    }
}