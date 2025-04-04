using System;

namespace SteamCmdWebAPI.Models
{
    /// <summary>
    /// Mô hình cài đặt server
    /// </summary>
    public class ServerSettings
    {
        /// <summary>
        /// Địa chỉ server TCP (IP hoặc hostname)
        /// </summary>
        public string ServerAddress { get; set; } = "localhost";
        
        /// <summary>
        /// Cổng kết nối TCP
        /// </summary>
        public int ServerPort { get; set; } = 61188;
        
        /// <summary>
        /// Cho phép đồng bộ với server
        /// </summary>
        public bool EnableServerSync { get; set; } = false;
        
        /// <summary>
        /// Thời gian đồng bộ lần cuối
        /// </summary>
        public DateTime? LastSyncTime { get; set; }
        
        /// <summary>
        /// Trạng thái kết nối
        /// </summary>
        public string ConnectionStatus { get; set; } = "Unknown";
    }
}