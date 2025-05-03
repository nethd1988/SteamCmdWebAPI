using System;

namespace SteamCmdWebAPI.Models
{
    public class AppVersionInfo
    {
        public string Version { get; set; } = "1.0.0";
        public DateTime ReleaseDate { get; set; } = DateTime.Now;
        public string DownloadUrl { get; set; } = string.Empty;
        public string ChangelogUrl { get; set; } = string.Empty;
        public string[] Changes { get; set; } = Array.Empty<string>();
        public bool IsCritical { get; set; } = false;
        public bool RequiresRestart { get; set; } = true;
    }
} 