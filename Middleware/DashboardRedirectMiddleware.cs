using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Middleware
{
    public class DashboardRedirectMiddleware
    {
        private readonly RequestDelegate _next;

        public DashboardRedirectMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Chuyển hướng từ / hoặc /api đến /Dashboard
            if (context.Request.Path == "/" || 
                context.Request.Path == "/api" || 
                context.Request.Path == "/Index" ||
                context.Request.Path == "/api/Index")
            {
                context.Response.Redirect("/Dashboard");
                return;
            }

            await _next(context);
        }
    }

    // Extension method
    public static class DashboardRedirectMiddlewareExtensions
    {
        public static IApplicationBuilder UseDashboardRedirect(this IApplicationBuilder app)
        {
            return app.UseMiddleware<DashboardRedirectMiddleware>();
        }
    }
}