using Microsoft.AspNetCore.Http;
using SteamCmdWebAPI.Services;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Middleware
{
    public class FirstUserSetupMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<FirstUserSetupMiddleware> _logger;

        public FirstUserSetupMiddleware(RequestDelegate next, ILogger<FirstUserSetupMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, UserService userService)
        {
            string path = context.Request.Path.Value.ToLower();

            // Bỏ qua middleware này nếu đường dẫn nằm trong danh sách cho phép
            if (path.Contains("/register") ||
                path.Contains("/login") ||
                path.StartsWith("/css") ||
                path.StartsWith("/js") ||
                path.StartsWith("/images") ||
                path.StartsWith("/lib"))
            {
                await _next(context);
                return;
            }

            // Kiểm tra xem đã có người dùng nào chưa
            if (!userService.AnyUsers())
            {
                _logger.LogInformation("Chưa có người dùng nào, chuyển hướng đến trang đăng ký");
                context.Response.Redirect("/Register");
                return;
            }

            await _next(context);
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