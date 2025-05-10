using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SteamCmdWebAPI.Services
{
    /// <summary>
    /// Service để lưu trữ cache icon game giữa các trang
    /// </summary>
    public class IconCacheService
    {
        private readonly ILogger<IconCacheService> _logger;
        
        // Cache toàn cục cho icon, key là AppID, value là đường dẫn tương đối
        private static readonly ConcurrentDictionary<string, string> _iconCache = new ConcurrentDictionary<string, string>();

        public IconCacheService(ILogger<IconCacheService> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Lấy icon từ cache, nếu không có thì tải từ Steam
        /// </summary>
        public async Task<string> GetIconAsync(string appId, SteamIconService iconService)
        {
            if (string.IsNullOrEmpty(appId))
                return null;

            // Kiểm tra trong cache trước
            if (_iconCache.TryGetValue(appId, out var cachedPath))
                return cachedPath;

            try
            {
                // Không có trong cache, tải mới
                var iconPath = await iconService.GetGameIconAsync(appId);
                
                // Nếu tải được thì lưu vào cache
                if (!string.IsNullOrEmpty(iconPath))
                    _iconCache.TryAdd(appId, iconPath);

                return iconPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải icon cho AppID {AppId}", appId);
                return null;
            }
        }

        /// <summary>
        /// Thêm icon vào cache
        /// </summary>
        public void AddIconToCache(string appId, string iconPath)
        {
            if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(iconPath))
                _iconCache.TryAdd(appId, iconPath);
        }

        /// <summary>
        /// Kiểm tra icon có trong cache không
        /// </summary>
        public bool HasIconInCache(string appId)
        {
            return !string.IsNullOrEmpty(appId) && _iconCache.ContainsKey(appId);
        }

        /// <summary>
        /// Lấy icon từ cache, không tải mới nếu không có
        /// </summary>
        public string GetIconFromCache(string appId)
        {
            if (string.IsNullOrEmpty(appId))
                return null;

            _iconCache.TryGetValue(appId, out var cachedPath);
            return cachedPath;
        }

        /// <summary>
        /// Xóa icon khỏi cache
        /// </summary>
        public bool RemoveIconFromCache(string appId)
        {
            if (string.IsNullOrEmpty(appId))
                return false;

            return _iconCache.TryRemove(appId, out _);
        }

        /// <summary>
        /// Xóa toàn bộ cache
        /// </summary>
        public void ClearCache()
        {
            _iconCache.Clear();
        }
    }
} 