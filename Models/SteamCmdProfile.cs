using System;
using System.ComponentModel.DataAnnotations;

namespace SteamCmdWebAPI.Models
{
    public class SteamCmdProfile
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Tên Profile là bắt buộc")]
        public string Name { get; set; }

        [Required(ErrorMessage = "App ID là bắt buộc")]
        public string AppID { get; set; }

        [Required(ErrorMessage = "Đường dẫn cài đặt là bắt buộc")]
        public string InstallDirectory { get; set; }

        public string SteamUsername { get; set; }
        public string SteamPassword { get; set; }
        public string Arguments { get; set; }
        public bool ValidateFiles { get; set; }
        public bool AutoRun { get; set; }
        public bool AnonymousLogin { get; set; }
        public string Status { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? StopTime { get; set; }
        public int Pid { get; set; }
        public DateTime? LastRun { get; set; }

        public SteamCmdProfile()
        {
            Name = "";
            AppID = "";
            InstallDirectory = "";
            SteamUsername = "";
            SteamPassword = "";
            Arguments = "";
            Status = "Stopped";
            Pid = 0;
            ValidateFiles = false;
            AutoRun = false;
            AnonymousLogin = false;
        }
    }
}