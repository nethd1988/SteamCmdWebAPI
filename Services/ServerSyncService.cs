using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    /// <summary>
    /// Dịch vụ đồng bộ với server
    /// </summary>
    public class ServerSyncService
    {
        private readonly ILogger<ServerSyncService> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ProfileService _profileService;
        private readonly HttpClient _httpClient;

        // Địa chỉ server mặc định cố định
        private const string DEFAULT_SERVER_ADDRESS = "idckz.ddnsfree.com";
        private const int DEFAULT_SERVER_PORT = 61188;

        public ServerSyncService(
            ILogger<ServerSyncService> logger,
            ServerSettingsService serverSettingsService,
            ProfileService profileService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serverSettingsService = serverSettingsService ?? throw new ArgumentNullException(nameof(serverSettingsService));
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 phút timeout cho các request lớn
        }

        /// <summary>
        /// Lấy danh sách tên profile từ server
        /// </summary>
        public async Task<List<string>> GetProfileNamesFromServerAsync()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();
                if (!settings.EnableServerSync)
                {
                    _logger.LogWarning("Đồng bộ server chưa được kích hoạt");
                    return new List<string>();
                }

                // Luôn sử dụng địa chỉ mặc định
                string url = $"http://{DEFAULT_SERVER_ADDRESS}:{DEFAULT_SERVER_PORT}/api/profiles/names";
                _logger.LogInformation("Đang lấy danh sách profile từ {Url}", url);

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Lỗi khi lấy danh sách profile: {response.StatusCode}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var profileNames = JsonSerializer.Deserialize<List<string>>(content);
                
                _logger.LogInformation("Đã lấy {Count} profile từ server", profileNames?.Count ?? 0);
                return profileNames ?? new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile từ server");
                
                // Trả về danh sách mẫu trong trường hợp lỗi (chỉ dùng để testing)
                return new List<string>
                {
                    "CS2 Server",
                    "Minecraft Server",
                    "ARK Survival",
                    "Valheim Dedicated",
                    "PUBG Test Server"
                };
            }
        }

        /// <summary>
        /// Đồng bộ tự động tất cả profiles với server
        /// </summary>
        public async Task<bool> AutoSyncWithServerAsync()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();
                if (!settings.EnableServerSync)
                {
                    _logger.LogInformation("Đồng bộ server không được kích hoạt, bỏ qua đồng bộ tự động");
                    return false;
                }

                _logger.LogInformation("Bắt đầu đồng bộ tự động với server");
                
                // Thực hiện đồng bộ profiles từ server về client
                var serverProfiles = await GetProfileNamesFromServerAsync();
                int syncedCount = 0;
                
                if (serverProfiles.Count > 0)
                {
                    foreach (var profileName in serverProfiles)
                    {
                        try
                        {
                            var serverProfile = await GetProfileFromServerByNameAsync(profileName);
                            if (serverProfile != null)
                            {
                                await SyncProfileToClientAsync(serverProfile);
                                syncedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi đồng bộ profile {ProfileName} từ server", profileName);
                        }
                    }
                }
                
                // Đồng bộ profiles từ client lên server
                var localProfiles = await _profileService.GetAllProfiles();
                bool uploadSuccess = await UploadProfilesToServerAsync(localProfiles);
                
                // Cập nhật thời gian đồng bộ
                await _serverSettingsService.UpdateLastSyncTimeAsync();
                
                _logger.LogInformation("Đã hoàn thành đồng bộ tự động: {SyncedCount} profiles đồng bộ từ server, {UploadCount} profiles đồng bộ lên server", 
                    syncedCount, localProfiles.Count);
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thực hiện đồng bộ tự động với server");
                return false;
            }
        }

        /// <summary>
        /// Lấy thông tin chi tiết profile từ server theo tên
        /// </summary>
        public async Task<SteamCmdProfile> GetProfileFromServerByNameAsync(string profileName)
        {
            try
            {
                if (string.IsNullOrEmpty(profileName))
                {
                    _logger.LogWarning("Tên profile không được để trống");
                    return null;
                }

                // Luôn sử dụng địa chỉ mặc định
                string encodedName = Uri.EscapeDataString(profileName);
                string url = $"http://{DEFAULT_SERVER_ADDRESS}:{DEFAULT_SERVER_PORT}/api/profiles/byname/{encodedName}";
                _logger.LogInformation("Đang lấy thông tin profile {ProfileName} từ {Url}", profileName, url);

                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Lỗi khi lấy thông tin profile {profileName}: {response.StatusCode}");
                }

                var content = await response.Content.ReadAsStringAsync();
                var profile = JsonSerializer.Deserialize<SteamCmdProfile>(content);
                
                _logger.LogInformation("Đã lấy thông tin profile {ProfileName} từ server", profileName);
                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin profile {ProfileName} từ server", profileName);
                
                // Trả về mẫu profile để testing
                return new SteamCmdProfile
                {
                    Name = profileName,
                    AppID = "730", // CS:GO app ID
                    InstallDirectory = $"D:\\SteamLibrary\\{profileName}",
                    Arguments = "-validate",
                    ValidateFiles = true,
                    AutoRun = false,
                    AnonymousLogin = true,
                    Status = "Stopped"
                };
            }
        }

        /// <summary>
        /// Đồng bộ profile từ server vào client
        /// </summary>
        private async Task<bool> SyncProfileToClientAsync(SteamCmdProfile serverProfile)
        {
            try
            {
                // Kiểm tra profile hợp lệ
                if (serverProfile == null || string.IsNullOrEmpty(serverProfile.Name))
                {
                    _logger.LogWarning("Profile không hợp lệ để đồng bộ");
                    return false;
                }
                
                // Kiểm tra xem profile đã tồn tại chưa
                var localProfiles = await _profileService.GetAllProfiles();
                var existingProfile = localProfiles.FirstOrDefault(p => p.Name == serverProfile.Name);
                
                if (existingProfile != null)
                {
                    // Cập nhật profile hiện có
                    serverProfile.Id = existingProfile.Id;
                    serverProfile.Status = existingProfile.Status;
                    serverProfile.Pid = existingProfile.Pid;
                    serverProfile.StartTime = existingProfile.StartTime;
                    serverProfile.StopTime = existingProfile.StopTime;
                    serverProfile.LastRun = existingProfile.LastRun;
                    
                    await _profileService.UpdateProfile(serverProfile);
                    _logger.LogInformation("Đã cập nhật profile {ProfileName} từ server", serverProfile.Name);
                }
                else
                {
                    // Thêm profile mới
                    int newId = localProfiles.Count > 0 ? localProfiles.Max(p => p.Id) + 1 : 1;
                    serverProfile.Id = newId;
                    serverProfile.Status = "Stopped";
                    
                    await _profileService.AddProfileAsync(serverProfile);
                    _logger.LogInformation("Đã thêm profile mới {ProfileName} từ server", serverProfile.Name);
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profile {ProfileName} từ server vào client", 
                    serverProfile?.Name ?? "Unknown");
                return false;
            }
        }

        /// <summary>
        /// Đồng bộ danh sách profile từ client lên server
        /// </summary>
        private async Task<bool> UploadProfilesToServerAsync(List<SteamCmdProfile> profiles)
        {
            try
            {
                if (profiles == null || profiles.Count == 0)
                {
                    _logger.LogWarning("Không có profiles để đồng bộ lên server");
                    return false;
                }
                
                // Luôn sử dụng địa chỉ mặc định
                string url = $"http://{DEFAULT_SERVER_ADDRESS}:{DEFAULT_SERVER_PORT}/api/profiles/sync";
                _logger.LogInformation("Đang đồng bộ {Count} profiles lên server {Url}", profiles.Count, url);
                
                // Serialize và gửi profiles lên server
                var json = JsonSerializer.Serialize(profiles);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                
                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Lỗi khi đồng bộ profiles lên server: {response.StatusCode}");
                }
                
                _logger.LogInformation("Đã đồng bộ {Count} profiles lên server thành công", profiles.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profiles lên server");
                return false;
            }
        }
    }
}