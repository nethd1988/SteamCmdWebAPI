using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class QueueManagerModel : PageModel
    {
        private readonly ILogger<QueueManagerModel> _logger;
        private readonly QueueService _queueService;
        private readonly ProfileService _profileService;

        public List<QueueService.QueueItem> CurrentQueue { get; set; } = new List<QueueService.QueueItem>();
        public List<QueueService.QueueItem> QueueHistory { get; set; } = new List<QueueService.QueueItem>();
        public bool IsProcessing { get; set; }
        public int TotalQueueItems { get; set; }
        public int CompletedItems { get; set; }
        public int FailedItems { get; set; }
        public string StatusMessage { get; set; }
        public bool IsSuccess { get; set; } = true;

        public QueueManagerModel(
            ILogger<QueueManagerModel> logger,
            QueueService queueService,
            ProfileService profileService)
        {
            _logger = logger;
            _queueService = queueService;
            _profileService = profileService;
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
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi tải danh sách hàng đợi: {ex.Message}";
                IsSuccess = false;
            }
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

        // Thêm endpoint AJAX để cập nhật trạng thái hàng đợi
        public async Task<IActionResult> OnGetUpdateQueueStatusAsync()
        {
            try
            {
                // Tải lại từ file để đảm bảo dữ liệu mới nhất
                var (queue, history) = await _queueService.LoadQueueFromFileAsync();
                
                return new JsonResult(new { 
                    success = true, 
                    currentQueue = queue, 
                    queueHistory = history 
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
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi bắt đầu xử lý hàng đợi");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostStopProcessingAsync()
        {
            try
            {
                await _queueService.StopProcessing();
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng xử lý hàng đợi");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostClearQueueAsync()
        {
            try
            {
                await _queueService.ClearQueue();
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa hàng đợi");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostRemoveFromQueueAsync(int id)
        {
            try
            {
                bool result = await _queueService.RemoveFromQueue(id);
                return new JsonResult(new { success = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa mục khỏi hàng đợi");
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
                return new JsonResult(new { success = result });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm lại mục vào hàng đợi");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
    }
}