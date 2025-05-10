using System;
using System.Text.Json.Serialization;

namespace SteamCmdWebAPI.Models
{
    public class SteamCmdProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string AppID { get; set; } = string.Empty;
        public string InstallDirectory { get; set; } = string.Empty;
        public string SteamUsername { get; set; } = string.Empty;
        public string SteamPassword { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public bool ValidateFiles { get; set; }
        public bool AutoRun { get; set; }
        
        /// <summary>
        /// CHÚ Ý: Dự án này KHÔNG bao giờ sử dụng tài khoản anonymous cho Steam.
        /// Tất cả các game đều yêu cầu tài khoản thực có quyền truy cập.
        /// Thuộc tính này chỉ giữ lại để tương thích với codebase cũ nhưng luôn được đặt là false.
        /// </summary>
        public bool AnonymousLogin { get; set; } = false;
        
        public string Status { get; set; } = "Stopped";
        public DateTime StartTime { get; set; } = DateTime.Now;
        public DateTime StopTime { get; set; } = DateTime.Now;
        public int Pid { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? LastRun { get; set; }
        public string DependencyIDs { get; set; } = string.Empty;
    }
}