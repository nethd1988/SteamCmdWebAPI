using System;

namespace SteamCmdWebAPI.Services
{
    public class AutoRunConfiguration
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan Interval { get; set; } = TimeSpan.FromHours(12);
    }
}
