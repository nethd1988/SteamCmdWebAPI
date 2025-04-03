namespace SteamCmdWebAPI.Models
{
    public class AutoRunSettings
    {
        public bool AutoRunEnabled { get; set; }
        public string AutoRunInterval { get; set; } = "daily";
        public int ScheduledHour { get; set; } = 7; // Thêm ScheduledHour
    }
}