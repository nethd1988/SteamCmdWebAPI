namespace SteamCmdWebAPI.Models
{
    public class UpdateCheckSettings
    {
        public bool Enabled { get; set; } = true;
        public int IntervalMinutes { get; set; } = 60;
        public bool AutoUpdateProfiles { get; set; } = true;
    }
}