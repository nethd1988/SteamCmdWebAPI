using System;
using System.Collections.Generic;

namespace SteamCmdWebAPI.Models.Dependencies
{
    public class ProfileDependency
    {
        public int Id { get; set; }
        public int ProfileId { get; set; }  // ID của profile gốc
        public string MainAppId { get; set; } // AppID chính
        public List<DependentApp> DependentApps { get; set; } = new List<DependentApp>();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    public class DependentApp
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public bool NeedsUpdate { get; set; }
        public DateTime? LastUpdateCheck { get; set; }
    }
}