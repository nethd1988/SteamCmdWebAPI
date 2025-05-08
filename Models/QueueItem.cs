public class QueueItem
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string ProfileName { get; set; }
    public string AppId { get; set; }
    public string AppName { get; set; }
    public string Status { get; set; } = "Đang chờ";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public bool IsMainApp { get; set; }
    public string ParentAppId { get; set; }
    public int Order { get; set; }
    public string Error { get; set; }
    public long TotalSize { get; set; } // Tổng dung lượng cần tải (byte)
    public long DownloadedSize { get; set; } // Dung lượng đã tải (byte)
    
    // Thêm property để hiển thị thời gian theo định dạng đẹp
    public string FormattedCreatedAt => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    public string FormattedStartedAt => StartedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "N/A";
    public string FormattedCompletedAt => CompletedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "N/A";
    public string FormattedAppType => IsMainApp ? "Chính" : "Phụ";
    
    // Thêm property để hiển thị kích thước đã định dạng
    public string FormattedTotalSize => FormatFileSize(TotalSize);
    public string FormattedDownloadedSize => FormatFileSize(DownloadedSize);
    
    // Thêm thuộc tính để hiển thị tiến trình dưới dạng phần trăm
    public int ProgressPercentage => TotalSize > 0 ? (int)Math.Min(100, (DownloadedSize * 100 / TotalSize)) : 0;
    
    // Hàm định dạng kích thước file
    private string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        
        return string.Format("{0:0.##} {1}", len, sizes[order]);
    }
}