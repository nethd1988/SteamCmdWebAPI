using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;
using SteamCmdWebAPI.Models;
using System.IO;

namespace SteamCmdWebAPI.Pages
{
    public class DashboardModel : PageModel
    {
        private readonly ILogger<DashboardModel> _logger;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly SteamApiService _steamApiService;
        private readonly DependencyManagerService _dependencyManagerService;
        private readonly LicenseService _licenseService;

        public int RunningProfilesCount { get; set; }
        public int TotalProfilesCount { get; set; }
        public int TotalGamesCount { get; set; }
        public string TotalStorageUsed { get; set; }
        public int RecentUpdatesCount { get; set; }
        public List<SteamCmdService.LogEntry> RecentLogs { get; set; } = new List<SteamCmdService.LogEntry>();
        public List<string> ActivityDates { get; set; } = new List<string>();
        public List<int> ActivityCounts { get; set; } = new List<int>();
        public ViewLicenseDto LicenseInfo { get; set; }
        public string LicenseStatus { get; set; }
        public string LicenseUsername { get; set; }

        public DashboardModel(
            ILogger<DashboardModel> logger,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            SteamApiService steamApiService,
            DependencyManagerService dependencyManagerService,
            LicenseService licenseService)
        {
            _logger = logger;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _steamApiService = steamApiService;
            _dependencyManagerService = dependencyManagerService;
            _licenseService = licenseService;
        }

        public async Task OnGetAsync()
        {
            // Kiểm tra license
            var licenseResult = await _licenseService.ValidateLicenseAsync();
            LicenseInfo = licenseResult.License;
            LicenseStatus = licenseResult.IsValid ? "Hợp lệ" : "Không hợp lệ";
            LicenseUsername = _licenseService.GetLicenseUsername();

            var profiles = await _profileService.GetAllProfiles();
            TotalProfilesCount = profiles.Count;
            RunningProfilesCount = profiles.Count(p => p.Status == "Running");
            
            // Calculate total storage used and unique games count
            long totalBytes = 0;
            var uniqueAppIds = new HashSet<string>();
            
            foreach (var profile in profiles)
            {
                // Exclude App ID 228980 as it's not a game
                if (profile.AppID != "228980")
                {
                    uniqueAppIds.Add(profile.AppID);
                    
                    // Get size from app update info for main app
                    var appInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID);
                    if (appInfo != null && appInfo.SizeOnDisk > 0)
                    {
                        totalBytes += appInfo.SizeOnDisk;
                    }

                    // Get dependent apps from manifest
                    try
                    {
                        string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                        var dependentAppIds = await _dependencyManagerService.ScanDependenciesFromManifest(steamappsDir, profile.AppID);
                        
                        foreach (var depAppId in dependentAppIds)
                        {
                            if (depAppId != "228980")
                            {
                                uniqueAppIds.Add(depAppId);
                                
                                // Get size from app update info for dependent app
                                var depAppInfo = await _steamApiService.GetAppUpdateInfo(depAppId);
                                if (depAppInfo != null && depAppInfo.SizeOnDisk > 0)
                                {
                                    totalBytes += depAppInfo.SizeOnDisk;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đọc manifest cho profile {0} (AppID: {1})", profile.Name, profile.AppID);
                    }
                }
            }
            
            TotalGamesCount = uniqueAppIds.Count;
            TotalStorageUsed = FormatFileSize(totalBytes);

            // Get recent updates count (last 24 hours)
            RecentUpdatesCount = profiles.Count(p => 
                p.LastRun.HasValue && 
                (DateTime.Now - p.LastRun.Value).TotalHours <= 24);

            // Get recent logs
            RecentLogs = _steamCmdService.GetLogs()
                .OrderByDescending(l => l.Timestamp)
                .Take(10)
                .ToList();

            // Generate activity data for the last 7 days
            var today = DateTime.Today;
            for (int i = 6; i >= 0; i--)
            {
                var date = today.AddDays(-i);
                ActivityDates.Add(date.ToString("dd/MM"));
                
                // Count profile runs for this date
                var runCount = profiles.Count(p => 
                    p.LastRun.HasValue && 
                    p.LastRun.Value.Date == date);
                ActivityCounts.Add(runCount);
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