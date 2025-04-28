using Microsoft.AspNetCore.Http;
using SteamCmdWebAPI.Services;
using System.Threading.Tasks;
using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Collections.Concurrent;

namespace SteamCmdWebAPI.Middleware
{
    public class LicenseCheckMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly LicenseService _licenseService;
        private readonly ILogger<LicenseCheckMiddleware> _logger;
        private static readonly ConcurrentDictionary<string, DateTime> _blockedIPs = new ConcurrentDictionary<string, DateTime>();
        private static readonly object _lock = new object();
        private static bool _isLicenseValid = false;
        private static DateTime _lastCheck = DateTime.MinValue;
        private const int CHECK_INTERVAL_SECONDS = 30;
        private const int MAX_FAILED_ATTEMPTS = 5;
        private const int BLOCK_DURATION_MINUTES = 60;

        public LicenseCheckMiddleware(
            RequestDelegate next, 
            LicenseService licenseService,
            ILogger<LicenseCheckMiddleware> logger)
        {
            _next = next;
            _licenseService = licenseService;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // Kiểm tra IP có bị chặn không
                var clientIP = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (_blockedIPs.TryGetValue(clientIP, out DateTime blockUntil))
                {
                    if (DateTime.UtcNow < blockUntil)
                    {
                        _logger.LogWarning("Truy cập bị chặn từ IP {IP} cho đến {Time}", clientIP, blockUntil);
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsync("Access denied");
                        return;
                    }
                    else
                    {
                        _blockedIPs.TryRemove(clientIP, out _);
                    }
                }

                // Kiểm tra license chỉ sau một khoảng thời gian nhất định
                if ((DateTime.UtcNow - _lastCheck).TotalSeconds >= CHECK_INTERVAL_SECONDS)
                {
                    lock (_lock)
                    {
                        if ((DateTime.UtcNow - _lastCheck).TotalSeconds >= CHECK_INTERVAL_SECONDS)
                        {
                            var licenseResult = _licenseService.ValidateLicenseAsync().Result;
                            _isLicenseValid = licenseResult.IsValid || licenseResult.UsingCache;
                            _lastCheck = DateTime.UtcNow;
                            
                            if (!_isLicenseValid)
                            {
                                _logger.LogWarning("License không hợp lệ tại thời điểm {Time}", DateTime.UtcNow);
                            }
                        }
                    }
                }

                // Kiểm tra tính toàn vẹn của ứng dụng
                if (!ValidateApplicationIntegrity())
                {
                    _logger.LogError("Phát hiện can thiệp trái phép vào ứng dụng");
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsync("Access denied");
                    return;
                }

                // Cho phép truy cập các endpoint liên quan đến license
                if (IsLicenseEndpoint(context.Request.Path))
                {
                    await _next(context);
                    return;
                }

                // Kiểm tra license
                if (!_isLicenseValid)
                {
                    // Ghi log chi tiết về việc truy cập không hợp lệ
                    _logger.LogWarning(
                        "Truy cập bị từ chối - IP: {IP}, Path: {Path}, User-Agent: {UserAgent}",
                        clientIP,
                        context.Request.Path,
                        context.Request.Headers["User-Agent"].ToString()
                    );

                    // Chuyển hướng đến trang lỗi license
                    context.Response.Redirect("/LicenseError");
                    return;
                }

                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong LicenseCheckMiddleware");
                context.Response.StatusCode = 500;
                await context.Response.WriteAsync("Internal Server Error");
            }
        }

        private bool IsLicenseEndpoint(PathString path)
        {
            return path.StartsWithSegments("/api/license") ||
                   path.StartsWithSegments("/LicenseError") ||
                   path.StartsWithSegments("/Login") ||
                   path.StartsWithSegments("/Register");
        }

        private bool ValidateApplicationIntegrity()
        {
            try
            {
                // Kiểm tra tính toàn vẹn của các file quan trọng
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var criticalFiles = new[]
                {
                    Path.Combine(baseDir, "SteamCmdWebAPI.dll"),
                    Path.Combine(baseDir, "SteamCmdWebAPI.exe"),
                    Path.Combine(baseDir, "appsettings.json")
                };

                foreach (var file in criticalFiles)
                {
                    if (!File.Exists(file))
                    {
                        _logger.LogError("File quan trọng không tồn tại: {File}", file);
                        return false;
                    }

                    // Kiểm tra thời gian sửa đổi cuối cùng
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTime < DateTime.Now.AddDays(-7))
                    {
                        _logger.LogWarning("File {File} có thể đã bị can thiệp", file);
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra tính toàn vẹn của ứng dụng");
                return false;
            }
        }
    }
} 