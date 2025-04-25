using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
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

            _cacheFilePath = Path.Combine(dataDir, "steam_app_info_cache.json");
            LoadCachedAppInfo();
        }

        private void LoadCachedAppInfo()
        {
            try
            {
                if (File.Exists(_cacheFilePath))
                {
                    string json = File.ReadAllText(_cacheFilePath);
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
                _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>();
            }
        }

        public async Task SaveCachedAppInfo()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_cachedAppInfo, Formatting.Indented);
                await File.WriteAllTextAsync(_cacheFilePath, json);
                _logger.LogInformation("Đã lưu {0} bản ghi thông tin Steam vào cache", _cachedAppInfo.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cache thông tin Steam vào {0}", _cacheFilePath);
            }
        }

        // Sửa phương thức GetAppUpdateInfo
        public async Task<AppUpdateInfo> GetAppUpdateInfo(string appId, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(appId) || !int.TryParse(appId, out _))
            {
                _logger.LogWarning("Yêu cầu lấy thông tin Steam với AppID không hợp lệ: '{AppID}'", appId);
                return null;
            }

            AppUpdateInfo cachedInfo = null;
            bool isFromCache = false;

            // Kiểm tra cache
            if (_cachedAppInfo.TryGetValue(appId, out cachedInfo))
            {
                isFromCache = true;
                if (forceRefresh && cachedInfo != null)
                {
                    // Lưu ChangeNumber hiện tại làm giá trị lần kiểm tra trước nếu forceRefresh
                    cachedInfo = new AppUpdateInfo
                    {
                        AppID = cachedInfo.AppID,
                        Name = cachedInfo.Name,
                        LastUpdate = cachedInfo.LastUpdate,
                        LastUpdateDateTime = cachedInfo.LastUpdateDateTime,
                        UpdateDays = cachedInfo.UpdateDays,
                        HasRecentUpdate = cachedInfo.HasRecentUpdate,
                        Developer = cachedInfo.Developer,
                        Publisher = cachedInfo.Publisher,
                        LastCheckedChangeNumber = cachedInfo.ChangeNumber,
                        ChangeNumber = cachedInfo.ChangeNumber,
                        LastCheckedUpdateDateTime = cachedInfo.LastUpdateDateTime,
                        SizeOnDisk = cachedInfo.SizeOnDisk,
                        LastChecked = cachedInfo.LastChecked
                    };
                }
                else if ((DateTime.Now - cachedInfo.LastChecked).TotalMinutes < 10 && !forceRefresh)
                {
                    return cachedInfo;
                }
            }

            try
            {
                // Gọi API
                string url = $"https://api.steamcmd.net/v1/info/{appId}";
                HttpResponseMessage response = await _httpClient.GetAsync(url);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("Không tìm thấy thông tin cho AppID {AppID} (404 Not Found)", appId);
                    _cachedAppInfo.TryRemove(appId, out _);
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

                    // Lưu giá trị từ lần kiểm tra trước
                    if (isFromCache && cachedInfo != null)
                    {
                        appInfo.LastCheckedChangeNumber = forceRefresh ? cachedInfo.ChangeNumber : cachedInfo.LastCheckedChangeNumber;
                        appInfo.LastCheckedUpdateDateTime = forceRefresh ? cachedInfo.LastUpdateDateTime : cachedInfo.LastCheckedUpdateDateTime;
                    }
                    else
                    {
                        appInfo.LastCheckedChangeNumber = 0;
                        appInfo.LastCheckedUpdateDateTime = null;
                    }

                    // Thiết lập các thuộc tính từ API
                    appInfo.Name = gameData.common?.name?.ToString() ?? "Không xác định";
                    appInfo.ChangeNumber = gameData._change_number?.ToObject<long>() ?? 0;
                    appInfo.Developer = gameData.extended?.developer?.ToString() ?? "Không có thông tin";
                    appInfo.Publisher = gameData.extended?.publisher?.ToString() ?? "Không có thông tin";

                    // Lấy SizeOnDisk từ cache nếu có
                    if (cachedInfo != null)
                    {
                        appInfo.SizeOnDisk = cachedInfo.SizeOnDisk;
                    }

                    // Phân tích thời gian cập nhật
                    string timeUpdatedStr = gameData.depots?.branches?.@public?.timeupdated?.ToString();
                    if (long.TryParse(timeUpdatedStr, out long timestamp) && timestamp > 0)
                    {
                        DateTime updateTimeUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
                        DateTime updateTimeLocal = updateTimeUtc.ToLocalTime();
                        appInfo.LastUpdate = updateTimeLocal.ToString("dd/MM/yyyy HH:mm:ss zzz");
                        appInfo.LastUpdateDateTime = updateTimeLocal;
                        appInfo.UpdateDays = (int)Math.Floor((DateTime.Now - updateTimeLocal).TotalDays);
                        appInfo.HasRecentUpdate = appInfo.UpdateDays >= 0 && appInfo.UpdateDays <= 7;
                    }
                    else
                    {
                        appInfo.LastUpdate = "Không có thông tin";
                        appInfo.LastUpdateDateTime = null;
                        appInfo.UpdateDays = -1;
                        appInfo.HasRecentUpdate = false;
                    }

                    // Đánh dấu thời gian kiểm tra
                    appInfo.LastChecked = DateTime.Now;

                    // Cập nhật cache
                    _cachedAppInfo[appId] = appInfo;
                    await SaveCachedAppInfo();

                    return appInfo;
                }
                else
                {
                    _logger.LogWarning("Phản hồi API Steam thành công nhưng dữ liệu không hợp lệ hoặc không tìm thấy cho AppID {AppID}.", appId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin Steam App {AppID}: {Message}", appId, ex.Message);

                // Trả về cache cũ nếu có lỗi và cache tồn tại
                if (cachedInfo != null)
                {
                    _logger.LogWarning("Sử dụng cache cũ cho AppID {AppID} do lỗi", appId);
                    return cachedInfo;
                }

                return null;
            }
        }

        public async Task ClearCacheAsync()
        {
            try
            {
                _cachedAppInfo.Clear();
                await SaveCachedAppInfo();
                _logger.LogInformation("Đã xóa cache thông tin Steam.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa cache thông tin Steam");
                throw;
            }
        }

        // Cập nhật thông tin SizeOnDisk từ manifest
        public async Task UpdateSizeOnDiskFromManifest(string appId, long sizeOnDisk)
        {
            if (_cachedAppInfo.TryGetValue(appId, out var appInfo))
            {
                appInfo.SizeOnDisk = sizeOnDisk;
                _cachedAppInfo[appId] = appInfo;
                await SaveCachedAppInfo();
                _logger.LogInformation("Đã cập nhật SizeOnDisk ({0} bytes) cho AppID {1}", sizeOnDisk, appId);
            }
        }

        // Phương thức để so sánh ChangeNumber giữa hiện tại và lần trước
        public bool HasChangeNumberChanged(string appId)
        {
            if (_cachedAppInfo.TryGetValue(appId, out var appInfo))
            {
                return appInfo.LastCheckedChangeNumber > 0 && appInfo.ChangeNumber != appInfo.LastCheckedChangeNumber;
            }
            return false;
        }

        // Phương thức mới để so sánh thời gian cập nhật giữa hiện tại và lần trước
        public bool HasUpdateDateTimeChanged(string appId)
        {
            if (_cachedAppInfo.TryGetValue(appId, out var appInfo))
            {
                return appInfo.LastCheckedUpdateDateTime.HasValue &&
                       appInfo.LastUpdateDateTime.HasValue &&
                       appInfo.LastUpdateDateTime.Value != appInfo.LastCheckedUpdateDateTime.Value;
            }
            return false;
        }
    }
}