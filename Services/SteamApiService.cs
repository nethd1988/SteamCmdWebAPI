using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using Newtonsoft.Json;
using JsonException = Newtonsoft.Json.JsonException;


namespace SteamCmdWebAPI.Services
{
    public class SteamApiService
    {
        private readonly ILogger<SteamApiService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _cacheFilePath;
        private Dictionary<string, AppUpdateInfo> _cachedUpdateInfo = new Dictionary<string, AppUpdateInfo>();
        
        public SteamApiService(ILogger<SteamApiService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
            
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }
            
            _cacheFilePath = Path.Combine(dataDir, "steam_app_updates.json");
            LoadCachedUpdateInfo();
        }
        
        private void LoadCachedUpdateInfo()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    string json = File.ReadAllText(_cacheFilePath);
                    _cachedUpdateInfo = JsonConvert.DeserializeObject<Dictionary<string, AppUpdateInfo>>(json) ?? new Dictionary<string, AppUpdateInfo>();
                    _logger.LogInformation("Đã tải {0} bản ghi thông tin cập nhật Steam từ cache", _cachedUpdateInfo.Count);
                }
                else
                {
                    _cachedUpdateInfo = new Dictionary<string, AppUpdateInfo>();
                    _logger.LogInformation("Không tìm thấy tệp cache thông tin cập nhật Steam, sử dụng cache mới");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải cache thông tin cập nhật Steam từ {0}", _cacheFilePath);
                _cachedUpdateInfo = new Dictionary<string, AppUpdateInfo>();
            }
        }
        
        private async Task SaveCachedUpdateInfo()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_cachedUpdateInfo, Formatting.Indented);
                await File.WriteAllTextAsync(_cacheFilePath, json);
                _logger.LogInformation("Đã lưu {0} bản ghi thông tin cập nhật Steam vào cache", _cachedUpdateInfo.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cache thông tin cập nhật Steam vào {0}", _cacheFilePath);
            }
        }
        
        public async Task<AppUpdateInfo> GetAppUpdateInfo(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId) || !int.TryParse(appId, out _))
            {
                _logger.LogWarning("Yêu cầu lấy thông tin Steam với AppID không hợp lệ: '{AppID}'", appId);
                return null;
            }

            try
            {
                string url = $"https://api.steamcmd.net/v1/info/{appId}";
                HttpResponseMessage response = await _httpClient.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Không tìm thấy thông tin cho AppID {AppID} (404 Not Found)", appId);
                    return null;
                }

                response.EnsureSuccessStatusCode();

                string json = await response.Content.ReadAsStringAsync();
                dynamic data = JsonConvert.DeserializeObject(json);

                if (data?.status == "success" && data?.data?[appId] != null)
                {
                    var gameData = data.data[appId];
                    var appInfo = new AppUpdateInfo { AppID = appId };

                    appInfo.Name = gameData.common?.name?.ToString() ?? "Không xác định";
                    appInfo.ChangeNumber = gameData._change_number?.ToObject<long>() ?? 0;
                    appInfo.Developer = gameData.extended?.developer?.ToString() ?? "Không có thông tin";
                    appInfo.Publisher = gameData.extended?.publisher?.ToString() ?? "Không có thông tin";

                    string timeUpdatedStr = gameData.depots?.branches?.@public?.timeupdated?.ToString();
                    if (long.TryParse(timeUpdatedStr, out long timestamp) && timestamp > 0)
                    {
                        DateTime updateTimeUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
                        DateTime updateTimeLocal = updateTimeUtc.ToLocalTime();
                        appInfo.LastUpdate = updateTimeLocal.ToString("dd/MM/yyyy HH:mm:ss zzz");
                        appInfo.LastUpdateDateTime = updateTimeLocal;
                        appInfo.UpdateDays = (int)Math.Floor((DateTime.Now - updateTimeLocal).TotalDays);
                        appInfo.HasRecentUpdate = appInfo.UpdateDays >= 0 && appInfo.UpdateDays <= 7;
                        appInfo.LastChecked = DateTime.Now;
                    }
                    else
                    {
                        appInfo.LastUpdate = "Không có thông tin";
                        appInfo.LastUpdateDateTime = null;
                        appInfo.UpdateDays = -1;
                        appInfo.HasRecentUpdate = false;
                        appInfo.LastChecked = DateTime.Now;
                    }

                    // Cập nhật cache
                    _cachedUpdateInfo[appId] = appInfo;
                    await SaveCachedUpdateInfo();

                    return appInfo;
                }
                else
                {
                    _logger.LogWarning("Phản hồi API Steam thành công nhưng dữ liệu không hợp lệ hoặc không tìm thấy cho AppID {AppID}", appId);
                    return null;
                }
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogError(httpEx, "Lỗi HTTP khi lấy thông tin Steam App {AppID}: {Message}", appId, httpEx.Message);
                return null;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogError(jsonEx, "Lỗi JSON khi phân tích phản hồi Steam API cho App {AppID}: {Message}", appId, jsonEx.Message);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi không mong muốn khi lấy thông tin Steam App {AppID}: {Message}", appId, ex.Message);
                return null;
            }
        }

        public async Task<bool> HasAppUpdate(string appId)
        {
            // Kiểm tra xem đã có thông tin trong cache chưa
            if (_cachedUpdateInfo.TryGetValue(appId, out var cachedInfo))
            {
                // Nếu đã kiểm tra trong vòng 10 phút, sử dụng thông tin đã cache
                if ((DateTime.Now - cachedInfo.LastChecked).TotalMinutes < 10)
                {
                    _logger.LogInformation("Sử dụng thông tin cache cho AppID {AppID} (kiểm tra gần đây)", appId);
                    return false; // Giả sử không có cập nhật nếu mới kiểm tra
                }
            }

            // Lấy thông tin mới từ API
            var newInfo = await GetAppUpdateInfo(appId);
            if (newInfo == null)
            {
                _logger.LogWarning("Không thể lấy thông tin cập nhật cho AppID {AppID}", appId);
                return false;
            }

            // So sánh với thông tin cũ trong cache
            if (cachedInfo != null)
            {
                if (newInfo.ChangeNumber > cachedInfo.ChangeNumber)
                {
                    _logger.LogInformation("Phát hiện cập nhật cho AppID {AppID}: ChangeNumber mới {NewChange}, cũ {OldChange}", 
                        appId, newInfo.ChangeNumber, cachedInfo.ChangeNumber);
                    return true;
                }
                
                if (newInfo.LastUpdateDateTime.HasValue && cachedInfo.LastUpdateDateTime.HasValue)
                {
                    if (newInfo.LastUpdateDateTime > cachedInfo.LastUpdateDateTime)
                    {
                        _logger.LogInformation("Phát hiện cập nhật cho AppID {AppID}: Thời gian cập nhật mới {NewTime}, cũ {OldTime}", 
                            appId, newInfo.LastUpdateDateTime, cachedInfo.LastUpdateDateTime);
                        return true;
                    }
                }
            }
            else
            {
                // Nếu chưa có trong cache, coi như có cập nhật
                _logger.LogInformation("Không có thông tin cập nhật trước đó cho AppID {AppID}, coi như có cập nhật", appId);
                return true;
            }

            _logger.LogInformation("Không phát hiện cập nhật mới cho AppID {AppID}", appId);
            return false;
        }

        public async Task<Dictionary<string, bool>> CheckUpdatesForProfiles(List<SteamCmdProfile> profiles)
        {
            var results = new Dictionary<string, bool>();
            
            foreach (var profile in profiles)
            {
                if (string.IsNullOrEmpty(profile.AppID))
                {
                    continue;
                }
                
                bool hasUpdate = await HasAppUpdate(profile.AppID);
                results[profile.AppID] = hasUpdate;
            }
            
            return results;
        }
    }

    public class AppUpdateInfo
    {
        public string AppID { get; set; }
        public string Name { get; set; }
        public string LastUpdate { get; set; } = "Không có thông tin";
        public DateTime? LastUpdateDateTime { get; set; }
        public int UpdateDays { get; set; } // Days ago (-1 if unknown)
        public bool HasRecentUpdate { get; set; } // Updated within last 7 days?
        public string Developer { get; set; } = "Không có thông tin";
        public string Publisher { get; set; } = "Không có thông tin";
        public long ChangeNumber { get; set; }
        public DateTime LastChecked { get; set; } = DateTime.Now;
    }
}