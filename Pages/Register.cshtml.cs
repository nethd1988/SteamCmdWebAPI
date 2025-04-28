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
    public class RegisterModel : PageModel
    {
        private readonly ILogger<RegisterModel> _logger;
        private readonly UserService _userService;

        [BindProperty]
        public string Username { get; set; }

        [BindProperty]
        public string Password { get; set; }

        [BindProperty]
        public string ConfirmPassword { get; set; }

        public string ErrorMessage { get; set; }

        public RegisterModel(ILogger<RegisterModel> logger, UserService userService)
        {
            _logger = logger;
            _userService = userService;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Nếu đã đăng nhập, chuyển hướng đến trang dashboard
            if (User.Identity.IsAuthenticated)
            {
                return RedirectToPage("/Dashboard");
            }

            // Không tự động chuyển hướng nếu đã ở trang Register
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password) || string.IsNullOrEmpty(ConfirmPassword))
            {
                ErrorMessage = "Vui lòng nhập đầy đủ thông tin";
                return Page();
            }

            if (Password != ConfirmPassword)
            {
                ErrorMessage = "Mật khẩu xác nhận không khớp";
                return Page();
            }

            try
            {
                var user = await _userService.RegisterAsync(Username, Password);

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

                _logger.LogInformation("Người dùng {Username} đã đăng ký và đăng nhập thành công", Username);

                return RedirectToPage("/Dashboard");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đăng ký: {Message}", ex.Message);
                ErrorMessage = "Đã xảy ra lỗi: " + ex.Message;
                return Page();
            }
        }
    }
}