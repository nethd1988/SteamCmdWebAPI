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

        public List<QueueItem> CurrentQueue { get; private set; } = new List<QueueItem>();
        public List<QueueItem> QueueHistory { get; private set; } = new List<QueueItem>();
        public bool IsProcessing { get; private set; }

        public QueueManagerModel(
            ILogger<QueueManagerModel> logger,
            QueueService queueService,
            ProfileService profileService)
        {
            _logger = logger;
            _queueService = queueService;
            _profileService = profileService;
        }

        public void OnGet()
        {
            CurrentQueue = _queueService.GetQueue();
            QueueHistory = _queueService.GetQueueHistory();
            IsProcessing = CurrentQueue.Any(q => q.Status == "Đang xử lý");
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