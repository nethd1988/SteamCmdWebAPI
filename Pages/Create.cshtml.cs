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
        private readonly ServerSyncService _serverSyncService;
        private readonly ServerSettingsService _serverSettingsService;

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

        public List<string> ServerProfiles { get; set; } = new List<string>();

        public CreateModel(
            ProfileService profileService,
            EncryptionService encryptionService,
            ILogger<CreateModel> logger,
            ServerSyncService serverSyncService,
            ServerSettingsService serverSettingsService)
        {
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serverSyncService = serverSyncService ?? throw new ArgumentNullException(nameof(serverSyncService));
            _serverSettingsService = serverSettingsService ?? throw new ArgumentNullException(nameof(serverSettingsService));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Xóa StatusMessage để tránh hiển thị thông báo lỗi cũ
            StatusMessage = null;
            IsSuccess = false;

            // Lấy danh sách profile từ server
            try
            {
                ServerProfiles = await _serverSyncService.GetProfileNamesFromServerAsync();
                _logger.LogInformation("Đã lấy {Count} profiles từ server", ServerProfiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profiles từ server");
                ServerProfiles = new List<string>();
            }

            return Page();
        }

        // Thêm handler để lấy profile từ server
        public async Task<IActionResult> OnGetProfileFromServerAsync(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
            {
                return BadRequest(new { success = false, error = "Tên profile không được để trống" });
            }

            try
            {
                var profile = await _serverSyncService.GetProfileFromServerByNameAsync(profileName);
                if (profile != null)
                {
                    return new JsonResult(new { success = true, profile = profile });
                }
                else
                {
                    return NotFound(new { success = false, error = "Không tìm thấy profile" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy profile {ProfileName} từ server", profileName);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
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

                // Lấy lại danh sách profile từ server
                try
                {
                    ServerProfiles = await _serverSyncService.GetProfileNamesFromServerAsync();
                }
                catch
                {
                    ServerProfiles = new List<string>();
                }

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

                        // Lấy lại danh sách profile từ server
                        try
                        {
                            ServerProfiles = await _serverSyncService.GetProfileNamesFromServerAsync();
                        }
                        catch
                        {
                            ServerProfiles = new List<string>();
                        }

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

                // Đặt trạng thái ban đầu
                Profile.Status = "Stopped";
                Profile.StartTime = DateTime.Now;
                Profile.StopTime = DateTime.Now;
                Profile.LastRun = DateTime.UtcNow;
                Profile.Pid = 0;

                await _profileService.AddProfileAsync(Profile);

                _logger.LogInformation("Đã lưu profile thành công: {Name}", Profile.Name);
                TempData["Success"] = $"Đã thêm mới cấu hình {Profile.Name}";

                // Đồng bộ tự động sau khi thêm mới
                _ = Task.Run(async () => {
                    try
                    {
                        var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                        if (serverSettings.EnableServerSync)
                        {
                            // Đồng bộ profile vừa cập nhật lên server
                            await _serverSyncService.UploadProfileAsync(Profile);
                            _logger.LogInformation("Đã hoàn thành đồng bộ profile {ProfileName} lên server sau khi cập nhật", Profile.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đồng bộ tự động sau khi cập nhật profile");
                    }
                });

                return RedirectToPage("./Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu profile trong Create");
                StatusMessage = $"Đã xảy ra lỗi khi lưu profile: {ex.Message}";
                IsSuccess = false;

                // Lấy lại danh sách profile từ server
                try
                {
                    ServerProfiles = await _serverSyncService.GetProfileNamesFromServerAsync();
                }
                catch
                {
                    ServerProfiles = new List<string>();
                }

                return Page();
            }
        }

        // Thêm handler để lưu profile từ server
        public async Task<IActionResult> OnPostImportFromServerAsync(string profileName)
        {
            try
            {
                if (string.IsNullOrEmpty(profileName))
                {
                    return BadRequest(new { success = false, error = "Tên profile không được để trống" });
                }

                var serverProfile = await _serverSyncService.GetProfileFromServerByNameAsync(profileName);
                if (serverProfile == null)
                {
                    return NotFound(new { success = false, error = $"Không tìm thấy profile '{profileName}' trên server" });
                }

                // Kiểm tra xem profile đã tồn tại chưa
                var existingProfile = (await _profileService.GetAllProfiles())
                    .FirstOrDefault(p => p.Name == serverProfile.Name);

                if (existingProfile != null)
                {
                    return BadRequest(new { success = false, error = $"Profile '{profileName}' đã tồn tại trong hệ thống" });
                }

                // Thêm profile mới
                await _profileService.AddProfileAsync(serverProfile);

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã nhập profile '{profileName}' từ server thành công",
                    redirectUrl = "./Index"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi nhập profile {ProfileName} từ server", profileName);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
    }
}