namespace SteamCmdWebAPI.Models
{
    public class AutoRunSettings
    {
        public bool AutoRunEnabled { get; set; }
        
        // Đổi từ tần suất (daily, weekly, monthly) thành khoảng thời gian (số giờ)
        public int AutoRunIntervalHours { get; set; } = 12; // Mặc định 12 giờ
        
        // Hỗ trợ tương thích ngược với phiên bản cũ
        public string AutoRunInterval { get; set; } = "daily";
        
        public int ScheduledHour { get; set; } = 7; // Mặc định chạy lần đầu vào 7h
    }
}