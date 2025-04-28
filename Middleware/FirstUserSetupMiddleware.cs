using Microsoft.AspNetCore.Http;
using SteamCmdWebAPI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace SteamCmdWebAPI.Middleware
{
    public class FirstUserSetupMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<FirstUserSetupMiddleware> _logger;
        private readonly HashSet<string> _allowedPaths;

        public FirstUserSetupMiddleware(RequestDelegate next, ILogger<FirstUserSetupMiddleware> logger)
        {
            _next = next;
            _logger = logger;

            // Danh sách các đường dẫn cho phép mà không cần kiểm tra
            _allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "register",
                "login",
                "css",
                "js",
                "lib",
                "images",
                "favicon.ico",
                "api/auth",
                "error",
                "licenseerror",
                "loghub",
                "steamhub"
            };
        }

        public async Task InvokeAsync(HttpContext context, UserService userService)
        {
            try
            {
                // Đảm bảo path không null
                string path = context.Request.Path.Value ?? string.Empty;
                
                // Loại bỏ dấu / ở đầu và cuối
                path = path.Trim('/');
                
                _logger.LogInformation("Middleware: Đang xử lý đường dẫn: {Path}", path);

                // Chuyển hướng đến Dashboard từ trang chủ
                if (string.IsNullOrEmpty(path) || path.Equals("index", StringComparison.OrdinalIgnoreCase) || 
                    path.Equals("api", StringComparison.OrdinalIgnoreCase) || path.Equals("api/index", StringComparison.OrdinalIgnoreCase))
                {
                    // Kiểm tra xem người dùng đã đăng nhập hay chưa
                    if (context.User?.Identity?.IsAuthenticated == true)
                    {
                        _logger.LogInformation("Middleware: Chuyển hướng từ trang chủ đến Dashboard");
                        context.Response.Redirect("/Dashboard");
                        return;
                    }
                }

                // Kiểm tra đường dẫn nằm trong danh sách cho phép
                bool isAllowedPath = false;
                foreach (var allowedPath in _allowedPaths)
                {
                    if (path.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        isAllowedPath = true;
                        _logger.LogInformation("Middleware: Cho phép truy cập đường dẫn {Path} vì nó bắt đầu bằng {AllowedPath}", path, allowedPath);
                        break;
                    }
                }

                if (isAllowedPath)
                {
                    await _next(context);
                    return;
                }

                // Kiểm tra nếu đã đăng nhập
                if (context.User?.Identity?.IsAuthenticated == true)
                {
                    // Kiểm tra tính hợp lệ của người dùng
                    var userId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (!string.IsNullOrEmpty(userId) && int.TryParse(userId, out int id))
                    {
                        var user = userService.GetUserById(id);
                        if (user != null)
                        {
                            // Người dùng hợp lệ, cho phép truy cập
                            _logger.LogInformation("Middleware: Cho phép truy cập cho người dùng đã đăng nhập: {Username}", user.Username);
                            await _next(context);
                            return;
                        }
                        else
                        {
                            // Người dùng không tồn tại, đăng xuất
                            _logger.LogWarning("Middleware: Người dùng không tồn tại trong hệ thống, đăng xuất từ ID: {UserId}", userId);
                            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                        }
                    }
                }

                // Kiểm tra xem đã có người dùng nào chưa
                if (!userService.AnyUsers())
                {
                    _logger.LogInformation("Middleware: Chưa có người dùng nào, chuyển hướng đến trang đăng ký từ: {Path}", path);
                    context.Response.Redirect("/Register");
                    return;
                }
                else
                {
                    // Có người dùng nhưng chưa đăng nhập
                    _logger.LogInformation("Middleware: Chưa đăng nhập, chuyển hướng đến trang đăng nhập từ: {Path}", path);
                    var returnUrl = context.Request.Path + context.Request.QueryString;
                    context.Response.Redirect($"/Login?ReturnUrl={System.Net.WebUtility.UrlEncode(returnUrl)}");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Middleware: Lỗi trong FirstUserSetupMiddleware");
                throw;
            }
        }
    }

    // Extension method để dễ dàng sử dụng middleware
    public static class FirstUserSetupMiddlewareExtensions
    {
        public static IApplicationBuilder UseFirstUserSetup(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<FirstUserSetupMiddleware>();
        }
    }
}