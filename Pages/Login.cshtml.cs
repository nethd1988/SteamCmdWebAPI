using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;
using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Pages
{
    public class LoginModel : PageModel
    {
        private readonly ILogger<LoginModel> _logger;
        private readonly UserService _userService;

        [BindProperty]
        public string Username { get; set; }

        [BindProperty]
        public string Password { get; set; }

        public string ErrorMessage { get; set; }
        public bool ShowRegister { get; set; }

        public LoginModel(ILogger<LoginModel> logger, UserService userService)
        {
            _logger = logger;
            _userService = userService;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Kiểm tra nếu đã đăng nhập rồi
            if (User.Identity.IsAuthenticated)
            {
                return RedirectToPage("/Index");
            }

            // Kiểm tra nếu không có người dùng nào, chuyển hướng sang trang Register
            if (!_userService.AnyUsers())
            {
                ShowRegister = true;
                return Page();
            }

            ShowRegister = true; // Luôn hiển thị tùy chọn đăng ký
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password))
            {
                ErrorMessage = "Vui lòng nhập đầy đủ tên đăng nhập và mật khẩu";
                ShowRegister = true;
                return Page();
            }

            try
            {
                var user = await _userService.AuthenticateAsync(Username, Password);

                if (user == null)
                {
                    ErrorMessage = "Tên đăng nhập hoặc mật khẩu không chính xác";
                    ShowRegister = true;
                    return Page();
                }

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new Claim(ClaimTypes.Role, user.IsAdmin ? "Admin" : "User"),
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
                };

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    authProperties);

                _logger.LogInformation("Người dùng {Username} đã đăng nhập thành công", Username);

                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đăng nhập: {Message}", ex.Message);
                ErrorMessage = "Đã xảy ra lỗi: " + ex.Message;
                ShowRegister = true;
                return Page();
            }
        }
    }
}