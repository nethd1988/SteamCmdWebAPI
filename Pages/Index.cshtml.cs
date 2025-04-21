using System;
using System.Collections.Generic;
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
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly SteamCmdService _steamCmdService;
        private readonly ProfileService _profileService;

        public List<SteamCmdProfile> Profiles { get; set; } = new List<SteamCmdProfile>();

        public IndexModel(
            ILogger<IndexModel> logger,
            IHubContext<LogHub> hubContext,
            SteamCmdService steamCmdService,
            ProfileService profileService)
        {
            _logger = logger;
            _hubContext = hubContext;
            _steamCmdService = steamCmdService;
            _profileService = profileService;
        }

        public async Task OnGetAsync()
        {
            _logger.LogInformation("Đang tải danh sách profiles...");
            Profiles = await _profileService.GetAllProfiles();
            _logger.LogInformation("Đã tải {0} profiles", Profiles.Count);
        }

        public async Task<IActionResult> OnPostRunAsync(int profileId)
        {
            try
            {
                _logger.LogInformation("Nhận yêu cầu POST Run với profileId: {ProfileId}", profileId);

                if (profileId <= 0)
                {
                    _logger.LogWarning("profileId không hợp lệ: {ProfileId}", profileId);
                    return new JsonResult(new { success = false, error = "profileId không hợp lệ" }) { StatusCode = 400 };
                }

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {0}", profileId);
                    return new JsonResult(new { success = false, error = $"Không tìm thấy profile với ID {profileId}" }) { StatusCode = 404 };
                }

                bool success = await _steamCmdService.RunProfileAsync(profileId);
                if (!success)
                {
                    _logger.LogWarning("Không thể chạy cấu hình với ID {0}", profileId);
                    return new JsonResult(new { success = false, error = $"Không thể chạy cấu hình với ID {profileId}. Kiểm tra log để biết thêm chi tiết." }) { StatusCode = 500 };
                }

                _logger.LogInformation("Chạy profile {ProfileId} thành công", profileId);
                return new JsonResult(new { success = true, noAlert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD cho profile ID {0}", profileId);
                return new JsonResult(new { success = false, error = $"Lỗi khi chạy SteamCMD: {ex.Message}" }) { StatusCode = 500 };
            }
        }

        public async Task<IActionResult> OnPostRunAllAsync()
        {
            try
            {
                _logger.LogInformation("Nhận yêu cầu POST RunAll");

                var profiles = await _profileService.GetAllProfiles();
                if (!profiles.Any())
                {
                    _logger.LogWarning("Không có cấu hình nào để chạy");
                    return new JsonResult(new { success = false, error = "Không có cấu hình nào để chạy" }) { StatusCode = 404 };
                }

                await _steamCmdService.RunAllProfilesAsync();

                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã chạy tất cả cấu hình thành công");
                _logger.LogInformation("Chạy tất cả profile thành công");
                return new JsonResult(new { success = true, noAlert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tất cả cấu hình");
                return new JsonResult(new { success = false, error = $"Lỗi khi chạy tất cả cấu hình: {ex.Message}" }) { StatusCode = 500 };
            }
        }

        public async Task<IActionResult> OnPostStopAllAsync()
        {
            try
            {
                _logger.LogInformation("Nhận yêu cầu POST StopAll");

                await _steamCmdService.StopAllProfilesAsync();
                _logger.LogInformation("Dừng tất cả profile thành công");
                return new JsonResult(new { success = true, noAlert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả tiến trình");
                return new JsonResult(new { success = false, error = $"Lỗi khi dừng tất cả tiến trình: {ex.Message}" }) { StatusCode = 500 };
            }
        }

        public async Task<IActionResult> OnPostStopAsync(int profileId)
        {
            try
            {
                _logger.LogInformation("Nhận yêu cầu POST Stop với profileId: {ProfileId}", profileId);

                // Cập nhật trạng thái trước khi dừng tiến trình
                var profile = await _profileService.GetProfileById(profileId);
                if (profile != null)
                {
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0;
                    await _profileService.UpdateProfile(profile);
                }

                // Dừng tiến trình SteamCMD
                await _steamCmdService.StopAllProfilesAsync();
                _logger.LogInformation("Dừng profile {ProfileId} thành công", profileId);
                return new JsonResult(new { success = true, noAlert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tiến trình");
                return new JsonResult(new { success = false, error = $"Lỗi khi dừng tiến trình: {ex.Message}" }) { StatusCode = 500 };
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(int profileId)
        {
            try
            {
                _logger.LogInformation("Nhận yêu cầu POST Delete với profileId: {ProfileId}", profileId);

                bool success = await _profileService.DeleteProfile(profileId);
                if (!success)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {0} để xóa", profileId);
                    return new JsonResult(new { success = false, error = $"Không tìm thấy profile với ID {profileId} để xóa" }) { StatusCode = 404 };
                }

                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã xóa cấu hình với ID {profileId}");
                _logger.LogInformation("Xóa profile {ProfileId} thành công", profileId);
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa cấu hình với ID {0}", profileId);
                return new JsonResult(new { success = false, error = $"Lỗi khi xóa cấu hình: {ex.Message}" }) { StatusCode = 500 };
            }
        }
    }
}