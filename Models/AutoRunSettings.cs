namespace SteamCmdWebAPI.Models
{
    public class AutoRunSettings
    {
        public bool AutoRunEnabled { get; set; }

        // Khoảng thời gian chạy (số giờ)
        public int AutoRunIntervalHours { get; set; } = 12; // Mặc định 12 giờ

        // Hỗ trợ tương thích ngược với phiên bản cũ
        public string AutoRunInterval { get; set; } = "daily";
    }
}