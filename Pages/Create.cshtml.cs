using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class CreateModel : PageModel
    {
        private readonly ProfileService _profileService;
        private readonly EncryptionService _encryptionService;
        private readonly ILogger<CreateModel> _logger;

        [BindProperty]
        public SteamCmdProfile Profile { get; set; } = new SteamCmdProfile();

        [BindProperty]
        public string Username { get; set; } = string.Empty;

        [BindProperty]
        public string Password { get; set; } = string.Empty;

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public CreateModel(
            ProfileService profileService,
            EncryptionService encryptionService,
            ILogger<CreateModel> logger)
        {
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Xóa StatusMessage để tránh hiển thị thông báo lỗi cũ
            StatusMessage = null;
            IsSuccess = false;
            return Page();
        }

        public async Task<IActionResult> OnPostSaveProfileAsync()
        {
            _logger.LogInformation("OnPostSaveProfileAsync called");

            // Loại bỏ các trường không cần thiết khỏi ModelState
            ModelState.Remove("Profile.Arguments");
            if (Profile.AnonymousLogin)
            {
                ModelState.Remove("Username");
                ModelState.Remove("Password");
            }

            // Xóa thông báo lỗi cũ
            StatusMessage = null;

            // Kiểm tra validation
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                _logger.LogWarning("Validation failed with errors: {Errors}", string.Join(", ", errors));
                StatusMessage = "Vui lòng kiểm tra lại thông tin: " + string.Join(", ", errors);
                IsSuccess = false;
                return Page();
            }

            try
            {
                _logger.LogInformation("Bắt đầu lưu profile: {Name}, AppID: {AppID}, InstallDirectory: {InstallDirectory}", 
                    Profile.Name, Profile.AppID, Profile.InstallDirectory);

                if (!Profile.AnonymousLogin)
                {
                    try
                    {
                        Profile.SteamUsername = string.IsNullOrEmpty(Username) ? string.Empty : _encryptionService.Encrypt(Username);
                        Profile.SteamPassword = string.IsNullOrEmpty(Password) ? string.Empty : _encryptionService.Encrypt(Password);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi mã hóa thông tin đăng nhập");
                        StatusMessage = "Lỗi khi mã hóa thông tin đăng nhập: " + ex.Message;
                        IsSuccess = false;
                        return Page();
                    }
                }
                else
                {
                    Profile.SteamUsername = string.Empty;
                    Profile.SteamPassword = string.Empty;
                }

                if (string.IsNullOrEmpty(Profile.Arguments))
                {
                    Profile.Arguments = string.Empty;
                }

                await _profileService.AddProfileAsync(Profile);

                _logger.LogInformation("Đã lưu profile thành công: {Name}", Profile.Name);
                TempData["Success"] = $"Đã thêm mới cấu hình {Profile.Name}";
                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu profile trong Create");
                StatusMessage = $"Đã xảy ra lỗi khi lưu profile: {ex.Message}";
                IsSuccess = false;
                return Page();
            }
        }
    }
}