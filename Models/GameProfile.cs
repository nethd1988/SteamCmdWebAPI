using System;
using System.Text.Json.Serialization;

namespace SteamCmdWebAPI.Models
{
    public class GameProfile
    {
        /// <summary>
        /// ID của profile
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Tên của profile
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Mô tả cho profile
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// ID của ứng dụng/game trên Steam
        /// </summary>
        public string AppID { get; set; } = string.Empty;

        /// <summary>
        /// Các tham số bổ sung khi chạy SteamCMD
        /// </summary>
        public string Arguments { get; set; } = string.Empty;

        /// <summary>
        /// Tên người dùng Steam (không được lưu trữ)
        /// </summary>
        [JsonIgnore]
        public string? Username { get; set; }

        /// <summary>
        /// Mật khẩu Steam (không được lưu trữ)
        /// </summary>
        [JsonIgnore]
        public string? Password { get; set; }

        /// <summary>
        /// Tên người dùng đã mã hóa
        /// </summary>
        public string? EncryptedUsername { get; set; }

        /// <summary>
        /// Mật khẩu đã mã hóa
        /// </summary>
        public string? EncryptedPassword { get; set; }

        /// <summary>
        /// Đường dẫn cài đặt game
        /// </summary>
        public string InstallDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Thời gian tạo profile
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        /// <summary>
        /// Thời gian cập nhật cuối
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}