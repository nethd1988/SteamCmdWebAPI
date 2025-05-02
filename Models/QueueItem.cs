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
    
    // Thêm property để hiển thị thời gian theo định dạng đẹp
    public string FormattedCreatedAt => CreatedAt.ToString("dd/MM/yyyy HH:mm:ss");
    public string FormattedStartedAt => StartedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "N/A";
    public string FormattedCompletedAt => CompletedAt?.ToString("dd/MM/yyyy HH:mm:ss") ?? "N/A";
    public string FormattedAppType => IsMainApp ? "Chính" : "Phụ";
}