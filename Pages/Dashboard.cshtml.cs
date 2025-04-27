using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Pages
{
    public class DashboardModel : PageModel
    {
        private readonly ILogger<DashboardModel> _logger;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;

        public int RunningProfilesCount { get; set; }
        public int TotalProfilesCount { get; set; }
        public string TotalStorageUsed { get; set; }
        public int RecentUpdatesCount { get; set; }
        public List<SteamCmdService.LogEntry> RecentLogs { get; set; } = new List<SteamCmdService.LogEntry>();
        public List<string> ActivityDates { get; set; } = new List<string>();
        public List<int> ActivityCounts { get; set; } = new List<int>();

        public DashboardModel(
            ILogger<DashboardModel> logger,
            ProfileService profileService,
            SteamCmdService steamCmdService)
        {
            _logger = logger;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
        }

        public async Task OnGetAsync()
        {
            var profiles = await _profileService.GetAllProfiles();
            TotalProfilesCount = profiles.Count;
            RunningProfilesCount = profiles.Count(p => p.Status == "Running");
            
            // Calculate total storage used
            long totalBytes = 0;
            foreach (var profile in profiles)
            {
                // Giả định có cách lấy dung lượng từ profile
                // totalBytes += GetProfileStorageSize(profile);
            }
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