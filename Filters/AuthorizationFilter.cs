using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Filters
{
    public class RequireFirstUserSetupFilter : IPageFilter
    {
        private readonly UserService _userService;

        public RequireFirstUserSetupFilter(UserService userService)
        {
            _userService = userService;
        }

        public void OnPageHandlerSelected(PageHandlerSelectedContext context)
        {
        }

        public void OnPageHandlerExecuting(PageHandlerExecutingContext context)
        {
            // Nếu không có người dùng nào và không phải trang Login hoặc Register
            if (!_userService.AnyUsers() && 
                !context.HttpContext.Request.Path.Value.Contains("/Login") && 
                !context.HttpContext.Request.Path.Value.Contains("/Register"))
            {
                context.Result = new RedirectToPageResult("/Register");
            }
        }

        public void OnPageHandlerExecuted(PageHandlerExecutedContext context)
        {
        }
    }
}