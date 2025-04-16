using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Pages
{
    public class EditModel : PageModel
    {
        private readonly ILogger<EditModel> _logger;
        private readonly ProfileService _profileService;
        private readonly EncryptionService _encryptionService;

        [BindProperty]
        public SteamCmdProfile Profile { get; set; }

        [BindProperty]
        public string NewUsername { get; set; }

        [BindProperty]
        public string NewPassword { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public EditModel(
            ILogger<EditModel> logger,
            ProfileService profileService,
            EncryptionService encryptionService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
        }

        public async Task<IActionResult> OnGetAsync(int? id)
        {
            // Xóa StatusMessage để tránh hiển thị thông báo lỗi cũ
            StatusMessage = null;
            IsSuccess = false;

            if (id == null)
            {
                return NotFound();
            }

            Profile = await _profileService.GetProfileById(id.Value);
            if (Profile == null)
            {
                return NotFound();
            }

            return Page();
        }

        public async Task<IActionResult> OnPostSaveProfileAsync()
        {
            _logger.LogInformation("OnPostSaveProfileAsync called for profile ID {0}", Profile.Id);

            // Loại bỏ validation cho các trường không cần thiết
            ModelState.Remove("NewUsername");
            ModelState.Remove("NewPassword");
            ModelState.Remove("Profile.Arguments");

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
                var existingProfile = await _profileService.GetProfileById(Profile.Id);
                if (existingProfile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {Id}", Profile.Id);
                    return NotFound();
                }

                // Giữ nguyên các trường không thay đổi
                Profile.StartTime = existingProfile.StartTime;
                Profile.StopTime = existingProfile.StopTime;
                Profile.LastRun = existingProfile.LastRun;

                if (Profile.AnonymousLogin)
                {
                    _logger.LogInformation("Đã chọn chế độ đăng nhập ẩn danh, xóa thông tin đăng nhập cũ");
                    Profile.SteamUsername = string.Empty;
                    Profile.SteamPassword = string.Empty;
                }
                else
                {
                    _logger.LogInformation("Không sử dụng đăng nhập ẩn danh, cập nhật thông tin đăng nhập");

                    // Kiểm tra xem người dùng đã nhập username mới chưa
                    if (!string.IsNullOrEmpty(NewUsername))
                    {
                        _logger.LogInformation("Mã hóa tên đăng nhập mới: {Username}", NewUsername);
                        try
                        {
                            Profile.SteamUsername = _encryptionService.Encrypt(NewUsername);
                            _logger.LogInformation("Mã hóa thành công, độ dài: {Length}", Profile.SteamUsername.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi mã hóa tên đăng nhập: {Message}", ex.Message);
                            StatusMessage = "Lỗi khi mã hóa tên đăng nhập";
                            IsSuccess = false;
                            return Page();
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Không có tên đăng nhập mới, giữ nguyên tên đăng nhập cũ");
                        Profile.SteamUsername = existingProfile.SteamUsername;
                    }

                    // Kiểm tra xem người dùng đã nhập password mới chưa
                    if (!string.IsNullOrEmpty(NewPassword))
                    {
                        _logger.LogInformation("Mã hóa mật khẩu mới");
                        try
                        {
                            Profile.SteamPassword = _encryptionService.Encrypt(NewPassword);
                            _logger.LogInformation("Mã hóa mật khẩu thành công, độ dài: {Length}", Profile.SteamPassword.Length);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi mã hóa mật khẩu: {Message}", ex.Message);
                            StatusMessage = "Lỗi khi mã hóa mật khẩu";
                            IsSuccess = false;
                            return Page();
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Không có mật khẩu mới, giữ nguyên mật khẩu cũ");
                        Profile.SteamPassword = existingProfile.SteamPassword;
                    }

                    // Kiểm tra xem có thông tin đăng nhập không
                    if (string.IsNullOrEmpty(Profile.SteamUsername))
                    {
                        _logger.LogWarning("Không có tên đăng nhập và không dùng đăng nhập ẩn danh");
                        StatusMessage = "Không thể cập nhật: Phải cung cấp tên đăng nhập hoặc sử dụng đăng nhập ẩn danh";
                        IsSuccess = false;
                        return Page();
                    }
                }

                // Đảm bảo rằng trường Arguments không null
                if (string.IsNullOrEmpty(Profile.Arguments))
                {
                    Profile.Arguments = "";
                }

                _logger.LogInformation("Profile before update: Name={Name}, AppID={AppID}, AnonymousLogin={AnonymousLogin}, HasUsername={HasUsername}",
                    Profile.Name, Profile.AppID, Profile.AnonymousLogin, !string.IsNullOrEmpty(Profile.SteamUsername));

                await _profileService.UpdateProfile(Profile);

                _logger.LogInformation("Đã cập nhật profile thành công: {Id}", Profile.Id);
                TempData["Success"] = "Cấu hình đã được cập nhật thành công!";
                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật cấu hình với ID {Id}: {Message}", Profile.Id, ex.Message);
                StatusMessage = "Lỗi khi cập nhật cấu hình: " + ex.Message;
                IsSuccess = false;
                return Page();
            }
        }
    }
}