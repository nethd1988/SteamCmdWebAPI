using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using SteamCmdWebAPI.Hubs;

namespace SteamCmdWebAPI.Pages
{
    public class QueueManagerModel : PageModel
    {
        private readonly ILogger<QueueManagerModel> _logger;
        private readonly QueueService _queueService;
        private readonly ProfileService _profileService;
        private readonly SteamApiService _steamApiService;
        private readonly SteamIconService _steamIconService;
        private readonly IHubContext<LogHub> _hubContext;

        public List<QueueService.QueueItem> CurrentQueue { get; set; } = new List<QueueService.QueueItem>();
        public List<QueueService.QueueItem> QueueHistory { get; set; } = new List<QueueService.QueueItem>();
        public bool IsProcessing { get; set; }
        public int TotalQueueItems { get; set; }
        public int CompletedItems { get; set; }
        public int FailedItems { get; set; }
        public string StatusMessage { get; set; }
        public bool IsSuccess { get; set; } = true;
        public Dictionary<string, string> AppIcons { get; set; } = new Dictionary<string, string>();
        public List<string> RecentLogs { get; set; } = new List<string>();

        public QueueManagerModel(
            ILogger<QueueManagerModel> logger,
            QueueService queueService,
            ProfileService profileService,
            SteamApiService steamApiService,
            SteamIconService steamIconService,
            IHubContext<LogHub> hubContext)
        {
            _logger = logger;
            _queueService = queueService;
            _profileService = profileService;
            _steamApiService = steamApiService;
            _steamIconService = steamIconService;
            _hubContext = hubContext;
        }

        public async Task OnGetAsync()
        {
            try
            {
                // Tải trực tiếp từ file để đảm bảo dữ liệu mới nhất
                var (queue, history) = await _queueService.LoadQueueFromFileAsync();
                CurrentQueue = queue;
                QueueHistory = history;
                
                // Thêm thống kê
                TotalQueueItems = CurrentQueue.Count;
                CompletedItems = QueueHistory.Count(i => i.Status == "Hoàn thành");
                FailedItems = QueueHistory.Count(i => i.Status == "Lỗi");
                
                // Cập nhật lại trạng thái của hàng đợi
                await _queueService.UpdateQueueStatusAsync();
                
                // Lấy icon cho tất cả các game trong queue và history
                await LoadGameIcons();
                
                // Lấy log mới nhất
                RecentLogs = _queueService.GetRecentLogs(10);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi tải danh sách hàng đợi: {ex.Message}";
                IsSuccess = false;
            }
        }

        private async Task LoadGameIcons()
        {
            // Tạo danh sách các AppID cần lấy icon
            var appIds = new HashSet<string>();
            
            foreach (var item in CurrentQueue)
            {
                if (!string.IsNullOrEmpty(item.AppId))
                {
                    appIds.Add(item.AppId);
                }
            }
            
            foreach (var item in QueueHistory)
            {
                if (!string.IsNullOrEmpty(item.AppId))
                {
                    appIds.Add(item.AppId);
                }
            }

            // Lấy icon cho từng AppID
            AppIcons = new Dictionary<string, string>();
            foreach (var appId in appIds)
            {
                try
                {
                    var iconPath = await _steamIconService.GetGameIconAsync(appId);
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        AppIcons[appId] = iconPath;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể lấy icon cho AppID {AppId}", appId);
                }
            }
        }

        // Phương thức để lấy đường dẫn icon từ AppID
        public string GetIconPath(string appId)
        {
            if (!string.IsNullOrEmpty(appId) && AppIcons.TryGetValue(appId, out var iconPath))
            {
                return iconPath;
            }
            return null;
        }

        public IActionResult OnGetGetCurrentQueue()
        {
            try
            {
                var currentQueue = _queueService.GetQueue();
                return new JsonResult(new { success = true, data = currentQueue });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách hàng đợi hiện tại");
                return new JsonResult(new { success = false, message = "Lỗi khi lấy danh sách hàng đợi" });
            }
        }

        // Endpoint để lấy logs mới nhất
        public IActionResult OnGetGetRecentLogs(int count = 20)
        {
            try
            {
                var logs = _queueService.GetRecentLogs(count);
                return new JsonResult(new { success = true, logs = logs });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy logs mới nhất");
                return new JsonResult(new { success = false, message = "Lỗi khi lấy logs: " + ex.Message });
            }
        }

        // Thêm endpoint AJAX để cập nhật trạng thái hàng đợi
        public async Task<IActionResult> OnGetUpdateQueueStatusAsync()
        {
            try
            {
                // Tải lại từ file để đảm bảo dữ liệu mới nhất
                var (queue, history) = await _queueService.LoadQueueFromFileAsync();
                IsProcessing = queue.Any(q => q.Status == "Đang xử lý");
                
                // Cập nhật trạng thái của hàng đợi
                await _queueService.UpdateQueueStatusAsync();

                // Lấy icon cho tất cả các game trong queue và history
                await LoadGameIcons();
                
                // Lấy logs mới nhất
                var recentLogs = _queueService.GetRecentLogs(20);
                
                // Chuẩn bị dữ liệu để trả về cho JavaScript
                var queueData = queue.Select(item => new
                {
                    id = item.Id,
                    order = item.Order,
                    appId = item.AppId,
                    appName = item.AppName,
                    profileId = item.ProfileId,
                    status = item.Status,
                    createdAt = item.CreatedAt,
                    startedAt = item.StartedAt,
                    completedAt = item.CompletedAt,
                    iconPath = GetIconPath(item.AppId)
                }).ToList();

                var historyData = history.Select(item => new
                {
                    id = item.Id,
                    order = item.Order,
                    appId = item.AppId,
                    appName = item.AppName,
                    profileId = item.ProfileId,
                    status = item.Status,
                    createdAt = item.CreatedAt,
                    startedAt = item.StartedAt,
                    completedAt = item.CompletedAt,
                    iconPath = GetIconPath(item.AppId)
                }).ToList();
                
                return new JsonResult(new { 
                    success = true, 
                    currentQueue = queueData, 
                    queueHistory = historyData,
                    isProcessing = IsProcessing,
                    recentLogs = recentLogs
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật trạng thái hàng đợi");
                return new JsonResult(new { 
                    success = false, 
                    message = ex.Message 
                });
            }
        }

        public async Task<IActionResult> OnPostStartProcessingAsync()
        {
            try
            {
                _queueService.StartProcessing();
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã bắt đầu xử lý hàng đợi cập nhật");
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi bắt đầu xử lý hàng đợi");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi bắt đầu xử lý hàng đợi: {ex.Message}");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostStopProcessingAsync()
        {
            try
            {
                await _queueService.StopProcessing();
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã dừng xử lý hàng đợi cập nhật");
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng xử lý hàng đợi");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi dừng xử lý hàng đợi: {ex.Message}");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostClearQueueAsync()
        {
            try
            {
                await _queueService.ClearQueue();
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã xóa tất cả các mục trong hàng đợi");
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa hàng đợi");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi xóa hàng đợi: {ex.Message}");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostRemoveFromQueueAsync(int id)
        {
            try
            {
                bool result = await _queueService.RemoveFromQueue(id);
                if (result)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã xóa mục ID: {id} khỏi hàng đợi");
                }
                return new JsonResult(new { success = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa mục khỏi hàng đợi");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi xóa mục khỏi hàng đợi: {ex.Message}");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostRetryItemAsync(int profileId, string appId, bool isMainApp)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy profile" });
                }

                bool result = await _queueService.AddToQueue(profileId, appId, isMainApp);
                if (result)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã thêm lại {appId} vào hàng đợi");
                }
                return new JsonResult(new { success = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm lại mục vào hàng đợi");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi thêm lại mục vào hàng đợi: {ex.Message}");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
    }
}