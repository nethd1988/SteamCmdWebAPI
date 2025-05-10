using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public class FailedAccountManager
    {
        private readonly ILogger<FailedAccountManager> _logger;
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, (DateTime failTime, string errorType)>> _failedAccounts = 
            new ConcurrentDictionary<string, ConcurrentDictionary<string, (DateTime failTime, string errorType)>>();
        
        private const int InvalidPasswordTimeoutMinutes = 480; // 8 giờ
        private const int RateLimitTimeoutMinutes = 60; // 1 giờ
        private const int ConnectionErrorTimeoutMinutes = 15; // 15 phút
        
        public FailedAccountManager(ILogger<FailedAccountManager> logger)
        {
            _logger = logger;
            // Chạy task dọn dẹp định kỳ
            Task.Run(async () => await CleanupTask());
        }
        
        public void MarkAccountAsFailed(string appId, string username, string errorType)
        {
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(username))
                return;
                
            var accounts = _failedAccounts.GetOrAdd(appId, 
                _ => new ConcurrentDictionary<string, (DateTime, string)>(StringComparer.OrdinalIgnoreCase));
                
            accounts[username] = (DateTime.Now, errorType);
            _logger.LogWarning("Đánh dấu tài khoản {Username} thất bại cho AppID {AppId} với lỗi {ErrorType}", 
                username, appId, errorType);
        }
        
        public bool IsAccountFailed(string appId, string username)
        {
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(username))
                return false;
                
            if (_failedAccounts.TryGetValue(appId, out var accounts) && 
                accounts.TryGetValue(username, out var failInfo))
            {
                var (failTime, errorType) = failInfo;
                int timeoutMinutes = GetTimeoutForErrorType(errorType);
                
                // Kiểm tra thời gian timeout
                if (DateTime.Now - failTime < TimeSpan.FromMinutes(timeoutMinutes))
                {
                    return true; // Vẫn còn trong thời gian timeout
                }
                else
                {
                    // Hết timeout, xóa khỏi danh sách thất bại
                    accounts.TryRemove(username, out _);
                    return false;
                }
            }
            
            return false;
        }
        
        public List<string> FilterFailedAccounts(string appId, List<string> usernames)
        {
            if (string.IsNullOrEmpty(appId) || usernames == null || !usernames.Any())
                return new List<string>(usernames ?? Enumerable.Empty<string>());
                
            return usernames.Where(username => !IsAccountFailed(appId, username)).ToList();
        }
        
        private int GetTimeoutForErrorType(string errorType)
        {
            switch (errorType)
            {
                case "InvalidPassword":
                    return InvalidPasswordTimeoutMinutes;
                case "RateLimit":
                    return RateLimitTimeoutMinutes;
                case "NoConnection":
                    return ConnectionErrorTimeoutMinutes;
                default:
                    return 30; // Mặc định 30 phút
            }
        }
        
        private async Task CleanupTask()
        {
            while (true)
            {
                try
                {
                    CleanupExpiredEntries();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi dọn dẹp danh sách tài khoản thất bại");
                }
                
                await Task.Delay(TimeSpan.FromMinutes(5)); // Dọn dẹp 5 phút một lần
            }
        }
        
        private void CleanupExpiredEntries()
        {
            foreach (var appEntry in _failedAccounts)
            {
                string appId = appEntry.Key;
                var accounts = appEntry.Value;
                
                foreach (var accountEntry in accounts.ToArray())
                {
                    string username = accountEntry.Key;
                    var (failTime, errorType) = accountEntry.Value;
                    int timeoutMinutes = GetTimeoutForErrorType(errorType);
                    
                    if (DateTime.Now - failTime >= TimeSpan.FromMinutes(timeoutMinutes))
                    {
                        accounts.TryRemove(username, out _);
                        _logger.LogInformation("Đã xóa tài khoản {Username} khỏi danh sách thất bại cho AppID {AppId} (hết hạn)", 
                            username, appId);
                    }
                }
                
                // Nếu không còn tài khoản nào trong appId, xóa luôn appId
                if (accounts.IsEmpty)
                {
                    _failedAccounts.TryRemove(appId, out _);
                }
            }
        }
    }
} 