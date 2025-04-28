using Microsoft.AspNetCore.Http;
using SteamCmdWebAPI.Services;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Middleware
{
    public class LicenseCheckMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly LicenseService _licenseService;

        public LicenseCheckMiddleware(RequestDelegate next, LicenseService licenseService)
        {
            _next = next;
            _licenseService = licenseService;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var licenseResult = await _licenseService.ValidateLicenseAsync();

            // Cho phép truy cập các endpoint liên quan đến license
            if (context.Request.Path.StartsWithSegments("/api/license") ||
                context.Request.Path.StartsWithSegments("/LicenseError"))
            {
                await _next(context);
                return;
            }

            // Kiểm tra license
            if (!licenseResult.IsValid && !licenseResult.UsingCache)
            {
                // Chuyển hướng đến trang lỗi license
                context.Response.Redirect("/LicenseError");
                return;
            }

            await _next(context);
        }
    }
} 