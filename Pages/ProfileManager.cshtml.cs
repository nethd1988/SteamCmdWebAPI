using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace SteamCmdWebAPI.Pages
{
    public class ProfileManagerModel : PageModel
    {
        private readonly ILogger<ProfileManagerModel> _logger;
        private readonly ProfileService _profileService;
        private readonly SteamApiService _steamApiService;
        private readonly SteamCmdService _steamCmdService;

        public List<SteamCmdProfile> Profiles { get; set; } = new List<SteamCmdProfile>();
        public Dictionary<string, string> GameSizes { get; set; } = new Dictionary<string, string>();

        public ProfileManagerModel(
            ILogger<ProfileManagerModel> logger,
            ProfileService profileService,
            SteamApiService steamApiService,
            SteamCmdService steamCmdService)
        {
            _logger = logger;
            _profileService = profileService;
            _steamApiService = steamApiService;
            _steamCmdService = steamCmdService;
        }

        public async Task OnGetAsync()
        {
            Profiles = await _profileService.GetAllProfiles();

            foreach (var profile in Profiles)
            {
                var appInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID);
                if (appInfo != null && appInfo.SizeOnDisk > 0)
                {
                    GameSizes[profile.AppID] = FormatFileSize(appInfo.SizeOnDisk);
                }
                else
                {
                    GameSizes[profile.AppID] = "N/A";
                }
            }
        }

        public async Task<IActionResult> OnPostRunAllAsync()
        {
            try
            {
                // Sử dụng RunAllProfilesAsync để chạy tất cả profile và cập nhật tất cả ID (chính và phụ thuộc)
                await _steamCmdService.RunAllProfilesAsync();
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
                await _steamCmdService.StopAllProfilesAsync();
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả profile");
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostRunAsync(int profileId)
        {
            try
            {
                // Sử dụng RunProfileAsync sẽ chỉ cập nhật app ID chính
                await _steamCmdService.RunProfileAsync(profileId);
                return new JsonResult(new { success = true });
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
                await _steamCmdService.StopProfileAsync(profileId);
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
                var result = await _profileService.DeleteProfile(profileId);
                if (result)
                {
                    return new JsonResult(new { success = true });
                }
                return new JsonResult(new { success = false, error = "Không tìm thấy profile" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa profile {ProfileId}", profileId);
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}