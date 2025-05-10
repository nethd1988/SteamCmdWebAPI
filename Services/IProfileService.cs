using System.Collections.Generic;
using System.Threading.Tasks;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public interface IProfileService
    {
        Task<List<SteamCmdProfile>> GetProfiles();
        Task<SteamCmdProfile> GetProfile(int id);
        Task<bool> UpdateProfile(SteamCmdProfile profile);
        Task<(string username, string password)> GetSteamAccountForAppId(string appId);
        // Add other methods as needed
    }
} 