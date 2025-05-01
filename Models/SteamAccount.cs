using System;
using System.Collections.Generic;

namespace SteamCmdWebAPI.Models
{
    public class SteamAccount
    {
        public int Id { get; set; }
        public string ProfileName { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string AppIds { get; set; } // Các AppId cách nhau bởi dấu phẩy
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}