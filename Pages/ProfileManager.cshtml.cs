using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class ProfileManagerModel : PageModel
    {
        private readonly ILogger<ProfileManagerModel> _logger;
        private readonly ProfileService _profileService;
        private readonly SteamApiService _steamApiService;

        public List<SteamCmdProfile> Profiles { get; set; } = new List<SteamCmdProfile>();
        public Dictionary<string, string> GameSizes { get; set; } = new Dictionary<string, string>();

        public ProfileManagerModel(
            ILogger<ProfileManagerModel> logger,
            ProfileService profileService,
            SteamApiService steamApiService)
        {
            _logger = logger;
            _profileService = profileService;
            _steamApiService = steamApiService;
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