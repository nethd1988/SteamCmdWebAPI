// Models/UpdateCheckSettings.cs
namespace SteamCmdWebAPI.Models
{
    public class UpdateCheckSettings
    {
        public bool Enabled { get; set; } = true;
        public int IntervalMinutes { get; set; } = 10;
        public bool AutoUpdateProfiles { get; set; } = true;
    }
}