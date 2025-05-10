using System;
using System.Text.Json.Serialization;

namespace SteamCmdWebAPI.Models
{
    public class ClientProfile
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string AppID { get; set; }
        public string InstallDirectory { get; set; }
        public string SteamUsername { get; set; }
        public string SteamPassword { get; set; }
        public string Arguments { get; set; }
        public bool ValidateFiles { get; set; }
        public bool AutoRun { get; set; }
        
        /// <summary>
        /// CHÚ Ý: Dự án này KHÔNG bao giờ sử dụng tài khoản anonymous cho Steam.
        /// Tất cả các game đều yêu cầu tài khoản thực có quyền truy cập.
        /// Thuộc tính này chỉ giữ lại để tương thích với codebase cũ nhưng luôn được đặt là false.
        /// </summary>
        public bool AnonymousLogin { get; set; } = false;
        
        public string Status { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime StopTime { get; set; }
        public int Pid { get; set; }
        public DateTime LastRun { get; set; }
    }
}