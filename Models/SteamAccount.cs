// Models/SteamAccount.cs
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SteamCmdWebAPI.Models
{
    public class SteamAccount
    {
        public int Id { get; set; }
        public string ProfileName { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string AppIds { get; set; } // Các AppId cách nhau bởi dấu phẩy
        public string GameNames { get; set; } // Tên game tương ứng với AppIds
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public bool AutoScanEnabled { get; set; } = false; // Trạng thái bật/tắt quét tự động
        
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? LastScanTime { get; set; } // Thời gian quét gần nhất
        
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public DateTime? NextScanTime { get; set; } // Thời gian quét tiếp theo dự kiến
        
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public int ScanIntervalHours { get; set; } = 6; // Khoảng thời gian giữa các lần quét (giờ)
    }
}