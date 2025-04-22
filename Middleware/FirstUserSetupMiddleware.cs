using Microsoft.AspNetCore.Http;
using SteamCmdWebAPI.Services;
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
            _allowedPaths = new HashSet<string>(new[]
            {
                "/register",
                "/login",
                "/css/",
                "/js/",
                "/lib/",
                "/images/",
                "/favicon.ico",
                "/api/auth/",
                "/error",
                "/licenseerror",
                "/loghub",
                "/steamhub"
            });
        }

        public async Task InvokeAsync(HttpContext context, UserService userService)
        {
            string path = context.Request.Path.Value?.ToLower() ?? "";

            // Kiểm tra đường dẫn nằm trong danh sách cho phép
            if (_allowedPaths.Any(ap => path.StartsWith(ap)))
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
                        await _next(context);
                        return;
                    }
                    else
                    {
                        // Người dùng không tồn tại, đăng xuất
                        _logger.LogWarning("Người dùng không tồn tại trong hệ thống, đăng xuất từ ID: {UserId}", userId);
                        await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                    }
                }
            }

            // Kiểm tra xem đã có người dùng nào chưa
            if (!userService.AnyUsers())
            {
                _logger.LogInformation("Chưa có người dùng nào, chuyển hướng đến trang đăng ký từ: {Path}", path);
                context.Response.Redirect("/Register");
                return;
            }
            else
            {
                // Có người dùng nhưng chưa đăng nhập
                _logger.LogInformation("Chưa đăng nhập, chuyển hướng đến trang đăng nhập từ: {Path}", path);
                var returnUrl = context.Request.Path + context.Request.QueryString;
                context.Response.Redirect($"/Login?ReturnUrl={System.Net.WebUtility.UrlEncode(returnUrl)}");
                return;
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