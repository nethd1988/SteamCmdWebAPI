using System;
using System.Collections.Generic;
using System.Collections.Concurrent; // Added for ConcurrentDictionary
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
        // Cache only stores the latest API info fetched, not comparison results
        // Changed from Dictionary to ConcurrentDictionary to use TryRemove
        private ConcurrentDictionary<string, AppUpdateInfo> _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>();

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

            _cacheFilePath = Path.Combine(dataDir, "steam_app_info_cache.json"); // Renamed cache file
            LoadCachedAppInfo();
        }

        private void LoadCachedAppInfo()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    string json = File.ReadAllText(_cacheFilePath);
                    // Deserialize into Dictionary first, then convert to ConcurrentDictionary
                    var loadedCache = JsonConvert.DeserializeObject<Dictionary<string, AppUpdateInfo>>(json);
                    _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>(loadedCache ?? new Dictionary<string, AppUpdateInfo>());
                    _logger.LogInformation("Đã tải {0} bản ghi thông tin Steam từ cache", _cachedAppInfo.Count);
                }
                else
                {
                    _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>();
                    _logger.LogInformation("Không tìm thấy tệp cache thông tin Steam, sử dụng cache mới");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải cache thông tin Steam từ {0}", _cacheFilePath);
                _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>(); // Ensure it's initialized even on error
            }
        }

        private async Task SaveCachedAppInfo()
        {
            try
            {
                // Serialize ConcurrentDictionary (JsonConvert handles this)
                string json = JsonConvert.SerializeObject(_cachedAppInfo, Formatting.Indented);
                await File.WriteAllTextAsync(_cacheFilePath, json);
                _logger.LogInformation("Đã lưu {0} bản ghi thông tin Steam vào cache", _cachedAppInfo.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cache thông tin Steam vào {0}", _cacheFilePath);
            }
        }

        /// <summary>
        /// Gets app update info from api.steamcmd.net, uses cache if recent.
        /// </summary>
        public async Task<AppUpdateInfo> GetAppUpdateInfo(string appId, bool forceRefresh = false) // Added forceRefresh
        {
            if (string.IsNullOrWhiteSpace(appId) || !int.TryParse(appId, out _))
            {
                _logger.LogWarning("Yêu cầu lấy thông tin Steam với AppID không hợp lệ: '{AppID}'", appId);
                return null;
            }

            // Check cache first if not forcing refresh and cache is recent
            if (!forceRefresh && _cachedAppInfo.TryGetValue(appId, out var cachedInfo))
            {
                // Use cache if checked within the last 10 minutes
                if ((DateTime.Now - cachedInfo.LastChecked).TotalMinutes < 10)
                {
                    _logger.LogDebug("Sử dụng thông tin cache cho AppID {AppID} (kiểm tra gần đây)", appId);
                    return cachedInfo;
                }
            }

            try
            {
                string url = $"https://api.steamcmd.net/v1/info/{appId}";
                HttpResponseMessage response = await _httpClient.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Không tìm thấy thông tin cho AppID {AppID} (404 Not Found)", appId);
                    // If not found, remove from cache using TryRemove
                    _cachedAppInfo.TryRemove(appId, out _); // Fixed: Now _cachedAppInfo is ConcurrentDictionary
                    await SaveCachedAppInfo();
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
                        appInfo.LastChecked = DateTime.Now; // Record when API was successfully called
                    }
                    else
                    {
                        appInfo.LastUpdate = "Không có thông tin";
                        appInfo.LastUpdateDateTime = null;
                        appInfo.UpdateDays = -1;
                        appInfo.HasRecentUpdate = false;
                        appInfo.LastChecked = DateTime.Now; // Record when API was successfully called
                    }

                    // Update cache with fresh info
                    _cachedAppInfo[appId] = appInfo; // Indexer works with ConcurrentDictionary
                    await SaveCachedAppInfo();

                    return appInfo;
                }
                else
                {
                    _logger.LogWarning("Phản hồi API Steam thành công nhưng dữ liệu không hợp lệ hoặc không tìm thấy cho AppID {AppID}. Phản hồi: {JsonResponse}", appId, json.Length > 500 ? json.Substring(0, 500) + "..." : json);
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
            catch (Exception ex) // Catch-all for other unexpected errors
            {
                _logger.LogError(ex, "Lỗi không mong muốn khi lấy thông tin Steam App {AppID}: {Message}", appId, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Clears the Steam API information cache.
        /// </summary>
        public async Task ClearCacheAsync() // Added this method
        {
            try
            {
                _cachedAppInfo.Clear(); // Clear the ConcurrentDictionary
                await SaveCachedAppInfo(); // Save the empty cache to file
                _logger.LogInformation("Đã xóa cache thông tin Steam.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa cache thông tin Steam");
                throw; // Re-throw for the calling method to handle
            }
        }


        // HasAppUpdate logic will be moved to UpdateCheckService for comparison with local manifest.
        // Keeping this method for now, but it might be removed or repurposed later.
        public async Task<bool> HasAppUpdate(string appId)
        {
            // This method now primarily checks if the API info itself indicates a recent update
            // based on the LastUpdateDateTime from the API.
            // The main logic for comparing API vs local manifest is in UpdateCheckService.

            var appInfo = await GetAppUpdateInfo(appId);
            if (appInfo?.LastUpdateDateTime != null)
            {
                // Check if the last update time from API is more recent than cached time (if any)
                if (_cachedAppInfo.TryGetValue(appId, out var cachedInfo) && cachedInfo.LastUpdateDateTime != null)
                {
                    if (appInfo.LastUpdateDateTime > cachedInfo.LastUpdateDateTime)
                    {
                        _logger.LogInformation("API reports a newer update time for AppID {AppID}", appId);
                        return true; // API shows a newer date
                    }
                }
                // If no cached info, or cached info is older, consider the API info as the latest known.
                // The actual check against the local manifest happens in UpdateCheckService.
            }

            return false; // Based on API time alone, no *new* API update time detected since last check
        }


        // Removed CheckUpdatesForProfiles as this logic moves to UpdateCheckService
        // public async Task<Dictionary<string, bool>> CheckUpdatesForProfiles(List<SteamCmdProfile> profiles) { ... }

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
        public DateTime LastChecked { get; set; } = DateTime.Now; // When API was last successfully called for this AppID
    }
}
