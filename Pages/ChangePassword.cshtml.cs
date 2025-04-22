using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    [Authorize]
    public class ChangePasswordModel : PageModel
    {
        private readonly ILogger<ChangePasswordModel> _logger;
        private readonly UserService _userService;

        [BindProperty]
        public string CurrentPassword { get; set; }

        [BindProperty]
        public string NewPassword { get; set; }

        [BindProperty]
        public string ConfirmPassword { get; set; }

        public string StatusMessage { get; set; }
        public bool IsSuccess { get; set; }

        public ChangePasswordModel(ILogger<ChangePasswordModel> logger, UserService userService)
        {
            _logger = logger;
            _userService = userService;
        }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrEmpty(CurrentPassword) || string.IsNullOrEmpty(NewPassword) || string.IsNullOrEmpty(ConfirmPassword))
            {
                StatusMessage = "Vui lòng nhập đầy đủ thông tin";
                IsSuccess = false;
                return Page();
            }

            if (NewPassword != ConfirmPassword)
            {
                StatusMessage = "Mật khẩu mới và mật khẩu xác nhận không khớp";
                IsSuccess = false;
                return Page();
            }

            try
            {
                int userId = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
                var result = await _userService.ChangePasswordAsync(userId, CurrentPassword, NewPassword);

                if (result)
                {
                    StatusMessage = "Mật khẩu đã được cập nhật thành công";
                    IsSuccess = true;
                    
                    // Xóa các trường nhập để ngăn việc gửi lại form
                    CurrentPassword = string.Empty;
                    NewPassword = string.Empty;
                    ConfirmPassword = string.Empty;
                }
                else
                {
                    StatusMessage = "Mật khẩu hiện tại không chính xác";
                    IsSuccess = false;
                }

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đổi mật khẩu: {Message}", ex.Message);
                StatusMessage = "Đã xảy ra lỗi: " + ex.Message;
                IsSuccess = false;
                return Page();
            }
        }
    }
}