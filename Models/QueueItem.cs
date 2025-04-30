using System;

namespace SteamCmdWebAPI.Models
{
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
    }
}