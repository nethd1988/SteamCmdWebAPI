using Microsoft.AspNetCore.Http;
using SteamCmdWebAPI.Services;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System;
using System.Linq;

namespace SteamCmdWebAPI.Middleware
{
    public class LicenseCheckMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly LicenseService _licenseService;
        private readonly ILogger<LicenseCheckMiddleware> _logger;
        private readonly LicenseStateService _licenseStateService;
        
        // Danh sách các endpoint LUÔN được phép truy cập ngay cả khi license không hợp lệ
        private readonly HashSet<string> _essentialEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/api/license",        // API kiểm tra license
            "/LicenseError",       // Trang thông báo lỗi license
            "/login",              // Trang đăng nhập
            "/logout",             // Trang đăng xuất
            "/register",           // Trang đăng ký
            "/error",              // Trang lỗi
            "/api/auth"            // API xác thực
        };

        // Các tài nguyên tĩnh luôn được phép truy cập
        private readonly string[] _staticResourcePrefixes = new string[]
        {
            "/css/",
            "/js/",
            "/lib/",
            "/images/",
            "/favicon.ico"
        };

        public LicenseCheckMiddleware(
            RequestDelegate next, 
            LicenseService licenseService, 
            ILogger<LicenseCheckMiddleware> logger,
            LicenseStateService licenseStateService)
        {
            _next = next;
            _licenseService = licenseService;
            _logger = logger;
            _licenseStateService = licenseStateService;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value.ToLowerInvariant();
            
            // Luôn cho phép truy cập tài nguyên tĩnh
            if (_staticResourcePrefixes.Any(prefix => path.StartsWith(prefix)))
            {
                await _next(context);
                return;
            }
            
            // Luôn cho phép truy cập các endpoint cần thiết
            if (_essentialEndpoints.Any(endpoint => path.StartsWith(endpoint, StringComparison.OrdinalIgnoreCase)))
            {
                await _next(context);
                return;
            }

            // Kiểm tra license từ dịch vụ trạng thái license
            // Chỉ cho phép truy cập nếu giấy phép hợp lệ HOẶC đang dùng cache trong grace period
            if (_licenseStateService.LockAllFunctions)
            {
                _logger.LogWarning("Khóa hoàn toàn truy cập vào {Path} do license không hợp lệ và không có cache", path);
                
                // Phân loại loại truy cập và xử lý phù hợp
                if (path.StartsWith("/api/"))
                {
                    // Nếu là API call, trả về lỗi 403
                    _logger.LogInformation("Trả về lỗi 403 cho API: {Path}", path);
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new { 
                        success = false, 
                        message = "Ứng dụng đã bị khóa hoàn toàn do giấy phép không hợp lệ",
                        licenseMessage = _licenseStateService.LicenseMessage,
                        code = "LICENSE_INVALID"
                    });
                    return;
                }
                else if (context.Request.Headers["X-Requested-With"] == "XMLHttpRequest" || 
                         context.Request.QueryString.ToString().Contains("handler="))
                {
                    // Nếu là AJAX request hoặc Page handler, trả về lỗi JSON
                    _logger.LogInformation("Trả về lỗi JSON cho AJAX request: {Path}", path);
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Ứng dụng đã bị khóa hoàn toàn do giấy phép không hợp lệ",
                        redirectUrl = "/LicenseError"
                    });
                    return;
                }
                else
                {
                    // Với các request thông thường, chuyển hướng đến trang lỗi license
                    _logger.LogInformation("Chuyển hướng đến trang lỗi license: {Path}", path);
                    context.Response.Redirect("/LicenseError");
                    return;
                }
            }

            // Nếu license hợp lệ hoặc đang trong grace period với cache, tiếp tục request
            await _next(context);
        }
    }
    
    // Extension method để đăng ký middleware
    public static class LicenseCheckMiddlewareExtensions
    {
        public static IApplicationBuilder UseLicenseCheck(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<LicenseCheckMiddleware>();
        }
    }
} 