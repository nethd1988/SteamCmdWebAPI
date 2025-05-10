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
        private readonly SteamIconService _steamIconService;
        private ConcurrentDictionary<string, AppUpdateInfo> _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>();

        public SteamApiService(ILogger<SteamApiService> logger, SteamIconService steamIconService)
        {
            _logger = logger;
            _steamIconService = steamIconService;
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
                    _logger.LogInformation($"Đã tải {_cachedAppInfo.Count} bản ghi thông tin Steam từ cache");
                }
                else
                {
                    _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>();
                    _logger.LogInformation("Không tìm thấy tệp cache thông tin Steam, sử dụng cache mới");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi tải cache thông tin Steam từ {_cacheFilePath}");
                _cachedAppInfo = new ConcurrentDictionary<string, AppUpdateInfo>();
            }
        }

        public async Task SaveCachedAppInfo()
        {
            try
            {
                string json = JsonConvert.SerializeObject(_cachedAppInfo, Formatting.Indented);
                await File.WriteAllTextAsync(_cacheFilePath, json);
                _logger.LogInformation($"Đã lưu {_cachedAppInfo.Count} bản ghi thông tin Steam vào cache");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi lưu cache thông tin Steam vào {_cacheFilePath}");
            }
        }

        // Sửa phương thức GetAppUpdateInfo
        public async Task<AppUpdateInfo> GetAppUpdateInfo(string appId, bool forceRefresh = false)
        {
            if (string.IsNullOrWhiteSpace(appId) || !int.TryParse(appId, out _))
            {
                _logger.LogWarning($"Yêu cầu lấy thông tin Steam với AppID không hợp lệ: '{appId}'");
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
                        UpdateSize = cachedInfo.UpdateSize,
                        LastChecked = cachedInfo.LastChecked,
                        IconPath = cachedInfo.IconPath
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
                    _logger.LogWarning($"Không tìm thấy thông tin cho AppID {appId} (404 Not Found)");
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
                        appInfo.IconPath = cachedInfo.IconPath; // Giữ lại đường dẫn icon từ cache
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

                    // Thử tải icon nếu chưa có
                    if (string.IsNullOrEmpty(appInfo.IconPath))
                    {
                        appInfo.IconPath = await _steamIconService.GetGameIconAsync(appId);
                    }

                    // Lấy kích thước ứng dụng
                    try 
                    {
                        // Cố gắng lấy thông tin kích thước từ SteamKit
                        if (gameData.depots != null)
                        {
                            long totalSize = 0;
                            long updateSize = 0;
                            
                            // Duyệt qua tất cả các depot
                            foreach (var depotProp in gameData.depots.Properties())
                            {
                                if (depotProp.Name == "branches" || depotProp.Name == "appinstalldir") continue;
                                
                                var depot = depotProp.Value;
                                // Lấy kích thước từ thuộc tính maxsize của depot
                                if (depot["maxsize"] != null)
                                {
                                    long depotSize = depot["maxsize"].ToObject<long>();
                                    totalSize += depotSize;
                                    _logger.LogInformation($"Thêm kích thước depot {depotProp.Name}: {depotSize} bytes");
                                }

                                // Lấy kích thước cập nhật từ dlcappid hoặc manifests
                                try
                                {
                                    if (depot["dlcappid"] != null)
                                    {
                                        // Đây là DLC, có thể cần cập nhật hoàn toàn
                                        long depotUpdateSize = depot["maxsize"]?.ToObject<long>() ?? 0;
                                        if (depotUpdateSize > 0)
                                        {
                                            updateSize += depotUpdateSize;
                                            _logger.LogInformation($"Thêm kích thước cập nhật DLC {depotProp.Name}: {depotUpdateSize} bytes");
                                        }
                                    }
                                    else if (depot["manifests"] != null)
                                    {
                                        // Kiểm tra các thuộc tính cập nhật trong manifest
                                        if (depot["manifests"]["public"] != null)
                                        {
                                            var manifest = depot["manifests"]["public"];
                                            
                                            // Ưu tiên kiểm tra bytes_to_download từ SteamKit 3.1
                                            if (manifest["bytes_to_download"] != null)
                                            {
                                                long bytesToDownload = manifest["bytes_to_download"].ToObject<long>();
                                                if (bytesToDownload > 0)
                                                {
                                                    updateSize += bytesToDownload;
                                                    _logger.LogInformation($"Thêm kích thước cập nhật bytes_to_download cho manifest {depotProp.Name}: {bytesToDownload} bytes");
                                                }
                                            }
                                            // Kiểm tra nếu có bytes_total và bytes_downloaded
                                            else if (manifest["bytes_total"] != null)
                                            {
                                                long bytesTotal = manifest["bytes_total"].ToObject<long>();
                                                
                                                if (manifest["bytes_downloaded"] != null)
                                                {
                                                    long bytesDownloaded = manifest["bytes_downloaded"].ToObject<long>();
                                                    long remainingBytes = bytesTotal - bytesDownloaded;
                                                    
                                                    if (remainingBytes > 0)
                                                    {
                                                        updateSize += remainingBytes;
                                                        _logger.LogInformation($"Thêm kích thước cập nhật (bytes_total - bytes_downloaded) cho manifest {depotProp.Name}: {remainingBytes} bytes");
                                                    }
                                                }
                                                else if (bytesTotal > 0)
                                                {
                                                    updateSize += bytesTotal;
                                                    _logger.LogInformation($"Thêm kích thước cập nhật bytes_total cho manifest {depotProp.Name}: {bytesTotal} bytes");
                                                }
                                            }
                                            // Dự phòng: sử dụng size từ manifest nếu không có các thuộc tính khác
                                            else if (manifest["size"] != null)
                                            {
                                                long manifestSize = manifest["size"].ToObject<long>();
                                                if (manifestSize > 0)
                                                {
                                                    updateSize += manifestSize;
                                                    _logger.LogInformation($"Thêm kích thước cập nhật manifest {depotProp.Name}: {manifestSize} bytes");
                                                }
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning($"Lỗi khi lấy kích thước cập nhật cho depot {depotProp.Name}: {ex.Message}");
                                }
                            }
                            
                            // Lấy kích thước cập nhật từ nhánh public nếu có
                            try
                            {
                                if (gameData.depots?.branches?.@public != null)
                                {
                                    var publicBranch = gameData.depots.branches.@public;
                                    
                                    // Dung lượng cập nhật từ buildid mới nhất
                                    if (publicBranch["buildid"] != null && cachedInfo != null && cachedInfo.ChangeNumber > 0)
                                    {
                                        string buildId = publicBranch["buildid"].ToString();
                                        _logger.LogInformation($"BuildID hiện tại cho {appId}: {buildId}");
                                        
                                        // Nếu ChangeNumber khác nhau, lấy kích thước cập nhật
                                        if (appInfo.ChangeNumber != cachedInfo.LastCheckedChangeNumber)
                                        {
                                            // Kiểm tra CMsgSystemUpdateProgress.stage_size_bytes nếu có
                                            if (publicBranch["update_progress"] != null && publicBranch["update_progress"]["stage_size_bytes"] != null)
                                            {
                                                long stageSize = publicBranch["update_progress"]["stage_size_bytes"].ToObject<long>();
                                                if (stageSize > 0)
                                                {
                                                    updateSize = stageSize;
                                                    _logger.LogInformation($"Sử dụng stage_size_bytes từ update_progress làm kích thước cập nhật: {stageSize} bytes");
                                                }
                                            }
                                            // Trong SteamKit 3.1, sử dụng trường bytes_to_download để lấy kích thước cập nhật
                                            else if (publicBranch["bytes_to_download"] != null)
                                            {
                                                long bytesToDownload = publicBranch["bytes_to_download"].ToObject<long>();
                                                if (bytesToDownload > 0)
                                                {
                                                    updateSize = bytesToDownload;
                                                    _logger.LogInformation($"Sử dụng bytes_to_download làm kích thước cập nhật: {bytesToDownload} bytes");
                                                }
                                            }
                                            // Thêm kiểm tra trường bytes_total từ SteamKit
                                            else if (publicBranch["bytes_total"] != null)
                                            {
                                                long bytesTotal = publicBranch["bytes_total"].ToObject<long>();
                                                if (bytesTotal > 0)
                                                {
                                                    // Nếu có cả bytes_downloaded, tính kích thước cập nhật từ hiệu của bytes_total và bytes_downloaded
                                                    if (publicBranch["bytes_downloaded"] != null)
                                                    {
                                                        long bytesDownloaded = publicBranch["bytes_downloaded"].ToObject<long>();
                                                        long remainingBytes = bytesTotal - bytesDownloaded;
                                                        if (remainingBytes > 0)
                                                        {
                                                            updateSize = remainingBytes;
                                                            _logger.LogInformation($"Tính kích thước cập nhật từ bytes_total - bytes_downloaded: {remainingBytes} bytes");
                                                        }
                                                    }
                                                    else
                                                    {
                                                        // Nếu không có bytes_downloaded, sử dụng bytes_total làm kích thước cập nhật
                                                        updateSize = bytesTotal;
                                                        _logger.LogInformation($"Sử dụng bytes_total làm kích thước cập nhật: {bytesTotal} bytes");
                                                    }
                                                }
                                            }
                                            // Sử dụng paksize làm dự phòng nếu các trường khác không có
                                            else if (publicBranch["paksize"] != null)
                                            {
                                                long pakSize = publicBranch["paksize"].ToObject<long>();
                                                if (pakSize > 0)
                                                {
                                                    updateSize = Math.Max(updateSize, pakSize);
                                                    _logger.LogInformation($"Sử dụng paksize làm kích thước cập nhật thay thế: {pakSize} bytes");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning($"Lỗi khi lấy kích thước cập nhật từ nhánh public: {ex.Message}");
                            }
                            
                            // Nếu không lấy được kích thước cập nhật, ước tính dựa trên thay đổi ChangeNumber
                            if (updateSize == 0 && cachedInfo != null && appInfo.ChangeNumber != cachedInfo.LastCheckedChangeNumber)
                            {
                                // Ước tính 5% kích thước tổng nếu có sự thay đổi
                                updateSize = (long)(totalSize * 0.05);
                                _logger.LogInformation($"Ước tính kích thước cập nhật là 5% kích thước tổng: {updateSize} bytes");
                            }
                            
                            if (totalSize > 0)
                            {
                                appInfo.SizeOnDisk = totalSize;
                                _logger.LogInformation($"Đã lấy kích thước tổng ứng dụng {appInfo.Name} ({appId}): {totalSize} bytes");
                            }
                            else if (cachedInfo != null && cachedInfo.SizeOnDisk > 0)
                            {
                                // Sử dụng kích thước từ cache nếu không lấy được mới
                                appInfo.SizeOnDisk = cachedInfo.SizeOnDisk;
                            }
                            
                            // Lưu kích thước cập nhật
                            if (updateSize > 0)
                            {
                                appInfo.UpdateSize = updateSize;
                                _logger.LogInformation($"Đã lấy kích thước cập nhật cho {appInfo.Name} ({appId}): {updateSize} bytes");
                            }
                            else if (cachedInfo != null && cachedInfo.UpdateSize > 0)
                            {
                                // Sử dụng kích thước cập nhật từ cache nếu không lấy được mới
                                appInfo.UpdateSize = cachedInfo.UpdateSize;
                            }
                        }
                        else if (cachedInfo != null)
                        {
                            // Sử dụng kích thước từ cache nếu không lấy được thông tin depot
                            if (cachedInfo.SizeOnDisk > 0)
                                appInfo.SizeOnDisk = cachedInfo.SizeOnDisk;
                            
                            if (cachedInfo.UpdateSize > 0)
                                appInfo.UpdateSize = cachedInfo.UpdateSize;
                        }
                    }
                    catch (Exception sizeEx)
                    {
                        _logger.LogWarning(sizeEx, $"Không thể lấy thông tin kích thước cho AppID {appId}: {sizeEx.Message}");
                        
                        // Vẫn giữ kích thước từ cache nếu có
                        if (cachedInfo != null)
                        {
                            if (cachedInfo.SizeOnDisk > 0)
                                appInfo.SizeOnDisk = cachedInfo.SizeOnDisk;
                            
                            if (cachedInfo.UpdateSize > 0)
                                appInfo.UpdateSize = cachedInfo.UpdateSize;
                        }
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
                    if (_cachedAppInfo.ContainsKey(appId))
                    {
                        _cachedAppInfo[appId] = appInfo;
                    }
                    else
                    {
                        _cachedAppInfo.TryAdd(appId, appInfo);
                    }

                    await SaveCachedAppInfo();

                    return appInfo;
                }
                else
                {
                    _logger.LogWarning($"Phản hồi API Steam thành công nhưng dữ liệu không hợp lệ hoặc không tìm thấy cho AppID {appId}.");
                    if (isFromCache && cachedInfo != null)
                    {
                        return cachedInfo; // Trả về thông tin từ cache nếu có lỗi
                    }
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi lấy thông tin Steam App {appId}: {ex.Message}");
                if (isFromCache && cachedInfo != null)
                {
                    return cachedInfo; // Trả về thông tin từ cache nếu có lỗi
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

        // Cập nhật thông tin kích thước cập nhật và kích thước tổng từ manifest
        public async Task UpdateSizeOnDiskFromManifest(string appId, long sizeOnDisk, long updateSize = 0)
        {
            if (_cachedAppInfo.TryGetValue(appId, out var appInfo))
            {
                appInfo.SizeOnDisk = sizeOnDisk;
                
                if (updateSize > 0)
                {
                    appInfo.UpdateSize = updateSize;
                    _logger.LogInformation($"Đã cập nhật UpdateSize ({updateSize} bytes) cho AppID {appId}");
                }
                
                _cachedAppInfo[appId] = appInfo;
                await SaveCachedAppInfo();
                _logger.LogInformation($"Đã cập nhật SizeOnDisk ({sizeOnDisk} bytes) cho AppID {appId}");
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

        /// <summary>
        /// Xóa cache thông tin cập nhật của một ứng dụng cụ thể
        /// </summary>
        public Task<bool> ClearAppUpdateCache(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId) || !int.TryParse(appId, out _))
            {
                _logger.LogWarning($"Yêu cầu xóa cache với AppID không hợp lệ: '{appId}'");
                return Task.FromResult(false);
            }

            try
            {
                // Xóa cache cho AppID này
                _cachedAppInfo.TryRemove(appId, out _);
                
                // Lưu lại cache mới
                SaveCachedAppInfo();
                
                _logger.LogInformation($"Đã xóa cache thông tin cập nhật cho AppID: {appId}");
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi xóa cache thông tin cập nhật cho AppID: {appId}");
                return Task.FromResult(false);
            }
        }

        // Phương thức để đăng ký nhận thông báo cập nhật từ Sever GL
        public async Task<bool> RegisterForAppUpdates(string appId, int profileId)
        {
            try
            {
                // Đảm bảo appId hợp lệ
                if (string.IsNullOrWhiteSpace(appId) || !int.TryParse(appId, out _))
                {
                    _logger.LogWarning($"Yêu cầu đăng ký cập nhật với AppID không hợp lệ: '{appId}'");
                    return false;
                }

                // Lấy thông tin cơ bản về ứng dụng
                var appInfo = await GetAppUpdateInfo(appId, forceRefresh: true);
                if (appInfo == null)
                {
                    _logger.LogWarning($"Không thể lấy thông tin cơ bản cho AppID {appId}");
                    return false;
                }

                // Ghi log là đã đăng ký thành công
                _logger.LogInformation($"Đã đăng ký nhận thông báo cập nhật cho AppID {appId} (Profile: {profileId})");
                
                // Trong triển khai thực tế, bạn sẽ sử dụng Sever GL để đăng ký các sự kiện cập nhật tại đây
                // Đây chỉ là phương thức placeholder và luôn trả về thành công

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi đăng ký nhận thông báo cập nhật cho AppID {appId} (Profile: {profileId})");
                return false;
            }
        }

        // Lấy danh sách các AppID đã đăng ký nhận thông báo từ SteamKit
        public async Task<List<string>> GetRegisteredAppIdsAsync()
        {
            try
            {
                _logger.LogInformation("Đang lấy danh sách AppID đã đăng ký với Sever GL");
                
                // Trong triển khai thực tế này, chúng ta sẽ coi như tất cả các AppID trong cache đã được đăng ký
                var registeredApps = _cachedAppInfo.Keys.ToList();
                
                if (registeredApps.Count == 0)
                {
                    _logger.LogInformation("Không tìm thấy AppID nào đã đăng ký với Sever GL");
                    return new List<string>();
                }
                
                _logger.LogInformation($"Đã tìm thấy {registeredApps.Count} AppID đã đăng ký với Sever GL");
                return registeredApps;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách AppID đã đăng ký với Sever GL");
                return new List<string>();
            }
        }
    }
}