using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;
using System.Collections.Generic;

namespace SteamCmdWebAPI.Filters
{
    public class RequireFirstUserSetupFilter : IPageFilter
    {
        private readonly UserService _userService;
        private readonly ILogger<RequireFirstUserSetupFilter> _logger;
        private readonly HashSet<string> _allowedPages;

        public RequireFirstUserSetupFilter(UserService userService, ILogger<RequireFirstUserSetupFilter> logger)
        {
            _userService = userService;
            _logger = logger;

            // Danh sách các trang cho phép truy cập mà không cần kiểm tra
            _allowedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Login",
                "Register",
                "Error",
                "LicenseError"
            };
        }

        public void OnPageHandlerSelected(PageHandlerSelectedContext context)
        {
            // Không làm gì
        }

        public void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            string pagePath = context.ActionDescriptor.DisplayName?.Split(' ')[0] ?? "";
            
            // Loại bỏ dấu / ở đầu và cuối nếu có
            pagePath = pagePath.Trim('/');
            
            _logger.LogInformation("Filter: Đang xử lý đường dẫn: {Page}", pagePath);

            // Cho phép các trang trong danh sách được truy cập tự do
            if (_allowedPages.Contains(pagePath))
            {
                _logger.LogInformation("Filter: Cho phép truy cập trang {Page} vì nó nằm trong danh sách cho phép", pagePath);
                return;
            }

            // Nếu người dùng đã đăng nhập, cho phép truy cập
            if (context.HttpContext.User?.Identity?.IsAuthenticated == true)
            {
                _logger.LogInformation("Filter: Cho phép truy cập trang {Page} vì người dùng đã đăng nhập", pagePath);
                return;
            }

            // Nếu chưa có người dùng nào và không phải trang Login hoặc Register
            if (!_userService.AnyUsers())
            {
                _logger.LogInformation("Filter: Chưa có người dùng nào, chuyển hướng đến trang đăng ký từ: {Page}", pagePath);
                context.Result = new RedirectToPageResult("/Register");
                return;
            }

            // Có người dùng nhưng chưa đăng nhập
            _logger.LogInformation("Filter: Chưa đăng nhập, chuyển hướng đến trang đăng nhập từ: {Page}", pagePath);
            context.Result = new RedirectToPageResult("/Login");
        }

        public void OnPageHandlerExecuted(PageHandlerExecutedContext context)
        {
            // Không làm gì
        }
    }
}