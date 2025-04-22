using Microsoft.AspNetCore.Http;
using SteamCmdWebAPI.Services;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Middleware
{
    public class FirstUserSetupMiddleware
    {
        private readonly RequestDelegate _next;

        public FirstUserSetupMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, UserService userService)
        {
            // Nếu không có người dùng nào và không phải đang ở trang Register
            if (!userService.AnyUsers() && 
                !context.Request.Path.StartsWithSegments("/Register") &&
                !context.Request.Path.StartsWithSegments("/Login") &&
                !context.Request.Path.StartsWithSegments("/css") &&
                !context.Request.Path.StartsWithSegments("/js") &&
                !context.Request.Path.StartsWithSegments("/images"))
            {
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