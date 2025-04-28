using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Middleware
{
    public class ApiErrorHandlingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<ApiErrorHandlingMiddleware> _logger;

        public ApiErrorHandlingMiddleware(RequestDelegate next, ILogger<ApiErrorHandlingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // Xử lý chuyển hướng API sang Dashboard
                if (context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/api/auth"))
                {
                    context.Response.Redirect("/Dashboard");
                    return;
                }

                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi không xử lý được trong API");
                
                // Nếu đường dẫn là API, trả về lỗi JSON
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = 500;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync($"{{\"error\": \"Đã xảy ra lỗi khi xử lý yêu cầu\", \"message\": \"{ex.Message}\"}}");
                }
                else
                {
                    // Chuyển hướng đến trang lỗi
                    context.Response.Redirect("/Error");
                }
            }
        }
    }

    // Extension method
    public static class ApiErrorHandlingMiddlewareExtensions
    {
        public static IApplicationBuilder UseApiErrorHandling(this IApplicationBuilder app)
        {
            return app.UseMiddleware<ApiErrorHandlingMiddleware>();
        }
    }
}