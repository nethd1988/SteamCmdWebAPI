using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Pages
{
    public class EditModel : PageModel
    {
        private readonly ILogger<EditModel> _logger;
        private readonly ProfileService _profileService;
        private readonly EncryptionService _encryptionService;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly TcpClientService _tcpClientService;

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
            EncryptionService encryptionService,
            ServerSettingsService serverSettingsService,
            TcpClientService tcpClientService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _serverSettingsService = serverSettingsService ?? throw new ArgumentNullException(nameof(serverSettingsService));
            _tcpClientService = tcpClientService ?? throw new ArgumentNullException(nameof(tcpClientService));
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
            _logger.LogInformation("OnPostSaveProfileAsync called");
            _logger.LogInformation("Profile before binding: Name={Name}, AppID={AppID}, InstallDirectory={InstallDirectory}, AnonymousLogin={AnonymousLogin}, ValidateFiles={ValidateFiles}, AutoRun={AutoRun}",
                Profile.Name, Profile.AppID, Profile.InstallDirectory, Profile.AnonymousLogin, Profile.ValidateFiles, Profile.AutoRun);
            _logger.LogInformation("NewUsername={NewUsername}, NewPassword={NewPassword}", NewUsername, NewPassword);

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

                Profile.StartTime = existingProfile.StartTime;
                Profile.StopTime = existingProfile.StopTime;
                Profile.LastRun = existingProfile.LastRun;

                if (Profile.AnonymousLogin)
                {
                    Profile.SteamUsername = string.Empty;
                    Profile.SteamPassword = string.Empty;
                }
                else
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(NewUsername))
                        {
                            Profile.SteamUsername = _encryptionService.Encrypt(NewUsername);
                        }
                        else
                        {
                            Profile.SteamUsername = existingProfile.SteamUsername;
                        }
                        if (!string.IsNullOrEmpty(NewPassword))
                        {
                            Profile.SteamPassword = _encryptionService.Encrypt(NewPassword);
                        }
                        else
                        {
                            Profile.SteamPassword = existingProfile.SteamPassword;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi mã hóa thông tin đăng nhập trong Edit");
                        StatusMessage = "Lỗi khi mã hóa thông tin đăng nhập: " + ex.Message;
                        IsSuccess = false;
                        return Page();
                    }
                }

                if (string.IsNullOrEmpty(Profile.Arguments))
                {
                    Profile.Arguments = "";
                }

                _logger.LogInformation("Profile before update: Name={Name}, AppID={AppID}, InstallDirectory={InstallDirectory}, AnonymousLogin={AnonymousLogin}, ValidateFiles={ValidateFiles}, AutoRun={AutoRun}",
                    Profile.Name, Profile.AppID, Profile.InstallDirectory, Profile.AnonymousLogin, Profile.ValidateFiles, Profile.AutoRun);

                await _profileService.UpdateProfile(Profile);

                _logger.LogInformation("Đã cập nhật profile thành công: {Id}", Profile.Id);
                TempData["Success"] = "Cấu hình đã được cập nhật thành công!";

                // Gửi profile về server
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (serverSettings.EnableServerSync)
                {
                    await _tcpClientService.SendProfileToServerAsync(Profile);
                    _logger.LogInformation("Đã gửi profile {Name} về server sau khi cập nhật", Profile.Name);
                }

                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật cấu hình với ID {Id}", Profile.Id);
                StatusMessage = "Lỗi khi cập nhật cấu hình: " + ex.Message;
                IsSuccess = false;
                return Page();
            }
        }
    }
}