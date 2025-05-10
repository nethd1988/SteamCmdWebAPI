using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public interface ISteamCmdService
    {
        Task<bool> RunProfileAsync(int id);
        Task<bool> RunSpecificAppAsync(int profileId, string appId);
        Task<bool> StopProfileAsync(int profileId);
        Task<bool> RestartProfileAsync(int profileId);
        Task<bool> QueueProfileForUpdate(int profileId);
        Task RunAllProfilesAsync();
        Task StopAllProfilesAsync();
        Task<bool> KillAllSteamCmdProcessesAsync();
        Task<bool> IsSteamCmdInstalled();
        Task InstallSteamCmd();
        Task ShutdownAsync();
        Task<bool> InvalidateAppUpdateCache(string appId);
        
        /// <summary>
        /// Xóa cache đăng nhập của Steam để tránh sử dụng lại thông tin đăng nhập cũ
        /// </summary>
        Task<bool> ClearLoginCacheAsync();
    }
} 