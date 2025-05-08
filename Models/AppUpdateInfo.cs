namespace SteamCmdWebAPI.Models
{
    public class AppUpdateInfo
    {
        public string AppID { get; set; }
        public string Name { get; set; }
        public string LastUpdate { get; set; } = "Không có thông tin";
        public DateTime? LastUpdateDateTime { get; set; }
        public DateTime? LastCheckedUpdateDateTime { get; set; } // Trường mới: Thời gian cập nhật từ lần kiểm tra trước
        public int UpdateDays { get; set; } // Days ago (-1 if unknown)
        public bool HasRecentUpdate { get; set; } // Updated within last 7 days?
        public string Developer { get; set; } = "Không có thông tin";
        public string Publisher { get; set; } = "Không có thông tin";
        public long ChangeNumber { get; set; }
        public long LastCheckedChangeNumber { get; set; } // Lưu ChangeNumber từ lần kiểm tra trước
        public long SizeOnDisk { get; set; } // Kích thước tổng trên đĩa
        public long UpdateSize { get; set; } // Kích thước cần cập nhật
        public DateTime LastChecked { get; set; } = DateTime.Now; // When API was last successfully called for this AppID
    }
}