using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SteamCmdWebAPI.Services
{
    public class SteamIconService
    {
        private readonly ILogger<SteamIconService> _logger;
        private readonly HttpClient _httpClient;
        private readonly string _iconCacheDir;
        private readonly IconCacheService _iconCacheService;

        public SteamIconService(ILogger<SteamIconService> logger, IconCacheService iconCacheService)
        {
            _logger = logger;
            _iconCacheService = iconCacheService;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            _httpClient.Timeout = TimeSpan.FromSeconds(10);

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _iconCacheDir = Path.Combine(baseDirectory, "wwwroot", "images", "game-icons");
            
            if (!Directory.Exists(_iconCacheDir))
            {
                Directory.CreateDirectory(_iconCacheDir);
            }
        }

        /// <summary>
        /// Tải xuống icon của game từ Steam API và lưu vào thư mục cache
        /// </summary>
        /// <param name="appId">ID của ứng dụng Steam</param>
        /// <returns>Đường dẫn tương đối đến icon, hoặc null nếu không tải được</returns>
        public async Task<string> GetGameIconAsync(string appId)
        {
            try
            {
                if (string.IsNullOrEmpty(appId) || !int.TryParse(appId, out _))
                {
                    _logger.LogWarning($"AppID không hợp lệ cho việc tải icon: {appId}");
                    return null;
                }
                
                // Kiểm tra cache toàn cục trước
                string cachedIconPath = _iconCacheService.GetIconFromCache(appId);
                if (!string.IsNullOrEmpty(cachedIconPath))
                {
                    return cachedIconPath;
                }

                string localIconPath = Path.Combine(_iconCacheDir, $"{appId}.jpg");
                string relativeIconPath = $"/images/game-icons/{appId}.jpg";

                // Kiểm tra xem đã có icon được cache trong thư mục chưa
                if (File.Exists(localIconPath))
                {
                    // Lưu vào cache toàn cục
                    _iconCacheService.AddIconToCache(appId, relativeIconPath);
                    return relativeIconPath;
                }

                // URL để lấy icon từ Steam
                string iconUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_sm_120.jpg";

                // Tải xuống icon
                var response = await _httpClient.GetAsync(iconUrl);
                
                if (response.IsSuccessStatusCode)
                {
                    using (var imageStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(localIconPath, FileMode.Create))
                    {
                        await imageStream.CopyToAsync(fileStream);
                    }

                    _logger.LogInformation($"Đã tải và lưu icon cho AppID: {appId}");
                    
                    // Lưu vào cache toàn cục
                    _iconCacheService.AddIconToCache(appId, relativeIconPath);
                    return relativeIconPath;
                }
                else
                {
                    _logger.LogWarning($"Không thể tải icon cho AppID {appId}: {response.StatusCode}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi tải icon cho AppID {appId}");
                return null;
            }
        }

        /// <summary>
        /// Tải lại icon của game, bỏ qua cache hiện tại
        /// </summary>
        /// <param name="appId">ID của ứng dụng Steam</param>
        /// <returns>Đường dẫn tương đối đến icon, hoặc null nếu không tải được</returns>
        public async Task<string> RefreshGameIconAsync(string appId)
        {
            try
            {
                if (string.IsNullOrEmpty(appId) || !int.TryParse(appId, out _))
                {
                    return null;
                }

                string localIconPath = Path.Combine(_iconCacheDir, $"{appId}.jpg");
                
                // Xóa file cũ nếu tồn tại
                if (File.Exists(localIconPath))
                {
                    File.Delete(localIconPath);
                }
                
                // Xóa khỏi cache
                _iconCacheService.RemoveIconFromCache(appId);

                // Tải lại icon
                var iconPath = await GetGameIconAsync(appId);
                return iconPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Lỗi khi tải lại icon cho AppID {appId}");
                return null;
            }
        }
    }
} 