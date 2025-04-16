using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Pages
{
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly SteamCmdService _steamCmdService;
        private readonly ProfileService _profileService;
        private readonly EncryptionService _encryptionService;
        private readonly ServerSyncService _serverSyncService;

        public List<SteamCmdProfile> Profiles { get; set; } = new List<SteamCmdProfile>();

        public IndexModel(
            ILogger<IndexModel> logger,
            IHubContext<LogHub> hubContext,
            SteamCmdService steamCmdService,
            ProfileService profileService,
            EncryptionService encryptionService,
            ServerSyncService serverSyncService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            _steamCmdService = steamCmdService ?? throw new ArgumentNullException(nameof(steamCmdService));
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _serverSyncService = serverSyncService ?? throw new ArgumentNullException(nameof(serverSyncService));
        }

        public async Task OnGetAsync()
        {
            try
            {
                _logger.LogInformation("Đang tải danh sách profiles");
                Profiles = await _profileService.GetAllProfiles();
                _logger.LogInformation("Đã tải {Count} profiles", Profiles.Count);

                // Tự động đồng bộ với server (chạy ngầm không chờ kết quả)
                _ = Task.Run(async () => {
                    try
                    {
                        await _serverSyncService.AutoSyncWithServerAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi tự động đồng bộ với server");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải danh sách profiles");
                TempData["Error"] = $"Lỗi khi tải dữ liệu: {ex.Message}";
            }
        }

        public async Task<IActionResult> OnPostRunAsync(int profileId)
        {
            try
            {
                _logger.LogInformation("Yêu cầu chạy profile ID {ProfileId}", profileId);

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {ProfileId}", profileId);
                    return new JsonResult(new { success = false, error = $"Không tìm thấy profile với ID {profileId}" });
                }

                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Bắt đầu chạy profile: {profile.Name} (ID: {profileId})");

                bool success = await _steamCmdService.RunProfileAsync(profileId);

                return new JsonResult(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy profile {ProfileId}", profileId);
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostStopAsync(int profileId)
        {
            try
            {
                _logger.LogInformation("Yêu cầu dừng profile ID {ProfileId}", profileId);

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {ProfileId}", profileId);
                    return new JsonResult(new { success = false, error = $"Không tìm thấy profile với ID {profileId}" });
                }

                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Dừng profile: {profile.Name} (ID: {profileId})");

                await _steamCmdService.StopProfileAsync(profileId);

                // Cập nhật trạng thái profile
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng profile {ProfileId}", profileId);
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostDeleteAsync(int profileId)
        {
            try
            {
                _logger.LogInformation("Yêu cầu xóa profile ID {ProfileId}", profileId);

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {ProfileId} để xóa", profileId);
                    return new JsonResult(new { success = false, error = $"Không tìm thấy profile với ID {profileId}" });
                }

                // Kiểm tra xem profile có đang chạy không
                if (profile.Status == "Running")
                {
                    _logger.LogWarning("Không thể xóa profile ID {ProfileId} vì đang chạy", profileId);
                    return new JsonResult(new { success = false, error = "Không thể xóa profile đang chạy. Vui lòng dừng profile trước." });
                }

                // Dừng profile nếu đang chạy (phòng hờ)
                await _steamCmdService.StopProfileAsync(profileId);

                bool success = await _profileService.DeleteProfile(profileId);
                if (success)
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã xóa profile: {profile.Name} (ID: {profileId})");

                    // Đồng bộ lên server sau khi xóa
                    _ = Task.Run(async () => {
                        try
                        {
                            await _serverSyncService.AutoSyncWithServerAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi đồng bộ với server sau khi xóa profile {ProfileId}", profileId);
                        }
                    });

                    return new JsonResult(new { success = true });
                }
                else
                {
                    _logger.LogWarning("Xóa profile ID {ProfileId} không thành công", profileId);
                    return new JsonResult(new { success = false, error = "Xóa profile không thành công" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa profile {ProfileId}", profileId);
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostRunAllAsync()
        {
            try
            {
                _logger.LogInformation("Yêu cầu chạy tất cả profile");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Bắt đầu chạy tất cả profile");

                var profiles = await _profileService.GetAllProfiles();
                _logger.LogInformation("Tìm thấy {Count} profile để chạy", profiles.Count);

                // Chỉ chạy các profile có trạng thái "Stopped" hoặc "Error"
                var profilesToRun = profiles.Where(p => p.Status != "Running").ToList();
                _logger.LogInformation("Sẽ chạy {Count} profile", profilesToRun.Count);

                // Chạy lệnh chạy tất cả không đợi kết quả
                _ = Task.Run(async () => {
                    try
                    {
                        await _steamCmdService.RunAllProfilesAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi chạy tất cả profile");
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi chạy tất cả profile: {ex.Message}");
                    }
                });

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tất cả profile");
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostStopAllAsync()
        {
            try
            {
                _logger.LogInformation("Yêu cầu dừng tất cả profile");
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Dừng tất cả profile");

                await _steamCmdService.StopAllProfilesAsync();

                // Cập nhật trạng thái tất cả profile
                var profiles = await _profileService.GetAllProfiles();
                foreach (var profile in profiles.Where(p => p.Status == "Running"))
                {
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    await _profileService.UpdateProfile(profile);
                }

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả profile");
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSubmitTwoFactorCodeAsync(int profileId, string twoFactorCode)
        {
            try
            {
                _logger.LogInformation("Nhận mã 2FA cho profile ID {ProfileId}", profileId);

                if (string.IsNullOrWhiteSpace(twoFactorCode))
                {
                    _logger.LogWarning("Mã 2FA trống cho profile ID {ProfileId}", profileId);
                    return new JsonResult(new { success = false, error = "Mã 2FA không được để trống" });
                }

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {ProfileId} khi gửi mã 2FA", profileId);
                    return new JsonResult(new { success = false, error = $"Không tìm thấy profile với ID {profileId}" });
                }

                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã nhận mã 2FA cho profile: {profile.Name} (ID: {profileId})");

                // Sử dụng SteamCmdService để xử lý mã 2FA
                // Tìm dòng code gọi phương thức này
                await _steamCmdService.SubmitTwoFactorCodeAsync(profileId, twoFactorCode);

                 return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý mã 2FA cho profile {ProfileId}", profileId);
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }
    }
}