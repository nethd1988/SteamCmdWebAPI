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
        private readonly LogService _logService;
        private const int PageSize = 20;

        public List<LogService.LogEntry> Logs { get; set; } = new List<LogService.LogEntry>();
        public int CurrentPage { get; set; } = 1;
        public int TotalPages { get; set; }
        public int TotalLogs { get; set; }

        public LogsModel(ILogger<LogsModel> logger, LogService logService)
        {
            _logger = logger;
            _logService = logService;
        }

        public void OnGet(int page = 1)
        {
            _logger.LogInformation("Đang tải danh sách logs...");
            
            // Lấy logs từ LogService
            Logs = _logService.GetLogs(page, PageSize);
            
            // Tính toán phân trang
            TotalLogs = Logs.Count;
            TotalPages = (int)Math.Ceiling(TotalLogs / (double)PageSize);
            CurrentPage = Math.Max(1, Math.Min(page, TotalPages));

            _logger.LogInformation("Đã tải {0} logs cho trang {1}/{2}", Logs.Count, CurrentPage, TotalPages);
        }
    }
}