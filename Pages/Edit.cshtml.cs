using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System;
using System.Collections.Generic;
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

        [BindProperty]
        public string SelectedServerProfile { get; set; }

        public List<SelectListItem> ServerProfiles { get; set; } = new List<SelectListItem>();

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public bool IsServerConfigured { get; set; }
        public string ServerAddress { get; set; }

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

            await LoadServerSettingsAsync();
            await LoadServerProfilesAsync();

            return Page();
        }

        private async Task LoadServerSettingsAsync()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();
                IsServerConfigured = settings.EnableServerSync && !string.IsNullOrEmpty(settings.ServerAddress);
                ServerAddress = settings.ServerAddress;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải cài đặt server");
                IsServerConfigured = false;
            }
        }

        private async Task LoadServerProfilesAsync()
        {
            ServerProfiles.Clear();
            ServerProfiles.Add(new SelectListItem
            {
                Value = "",
                Text = "-- Chọn profile từ server --",
                Selected = true
            });
            if (!IsServerConfigured)
            {
                return;
            }
            try
            {
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                var profileNames = await _tcpClientService.GetProfileNamesAsync(serverSettings.ServerAddress);
                foreach (var profileName in profileNames)
                {
                    ServerProfiles.Add(new SelectListItem
                    {
                        Value = profileName,
                        Text = profileName
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải danh sách profile từ server");
            }
        }

        public async Task<IActionResult> OnPostSaveProfileAsync()
        {
            _logger.LogInformation("OnPostSaveProfileAsync called");
            _logger.LogInformation("Profile before binding: Name={Name}, AppID={AppID}, InstallDirectory={InstallDirectory}, AnonymousLogin={AnonymousLogin}, ValidateFiles={ValidateFiles}, AutoRun={AutoRun}",
                Profile.Name, Profile.AppID, Profile.InstallDirectory, Profile.AnonymousLogin, Profile.ValidateFiles, Profile.AutoRun);
            _logger.LogInformation("NewUsername={NewUsername}, NewPassword={NewPassword}, SelectedServerProfile={SelectedServerProfile}", NewUsername, NewPassword, SelectedServerProfile);

            // Loại bỏ validation cho các trường không cần thiết
            ModelState.Remove("NewUsername");
            ModelState.Remove("NewPassword");
            ModelState.Remove("Profile.Arguments");
            ModelState.Remove("SelectedServerProfile"); // Loại bỏ validation cho SelectedServerProfile

            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                _logger.LogWarning("Validation failed with errors: {Errors}", string.Join(", ", errors));
                StatusMessage = "Vui lòng kiểm tra lại thông tin: " + string.Join(", ", errors);
                IsSuccess = false;
                await LoadServerSettingsAsync();
                await LoadServerProfilesAsync();
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
                        await LoadServerSettingsAsync();
                        await LoadServerProfilesAsync();
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
                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật cấu hình với ID {Id}", Profile.Id);
                StatusMessage = "Lỗi khi cập nhật cấu hình: " + ex.Message;
                IsSuccess = false;
                await LoadServerSettingsAsync();
                await LoadServerProfilesAsync();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostSyncWithServerAsync()
        {
            _logger.LogInformation("OnPostSyncWithServerAsync called");

            try
            {
                _logger.LogInformation($"Bắt đầu đồng bộ profile từ server: {SelectedServerProfile}");

                if (string.IsNullOrEmpty(SelectedServerProfile))
                {
                    StatusMessage = "Vui lòng chọn một profile từ server";
                    IsSuccess = false;
                    await LoadServerSettingsAsync();
                    await LoadServerProfilesAsync();
                    return Page();
                }

                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                _logger.LogInformation($"Đã lấy cài đặt server, đang kết nối tới: {serverSettings.ServerAddress}");

                var serverProfile = await _tcpClientService.GetProfileDetailsByNameAsync(serverSettings.ServerAddress, SelectedServerProfile, serverSettings.ServerPort);
                _logger.LogInformation($"Đã lấy profile từ server: {serverProfile?.Name ?? "null"}");

                if (serverProfile == null)
                {
                    StatusMessage = $"Không tìm thấy profile '{SelectedServerProfile}' trên server";
                    IsSuccess = false;
                    await LoadServerSettingsAsync();
                    await LoadServerProfilesAsync();
                    return Page();
                }

                // Cập nhật thông tin profile từ server
                Profile.Name = serverProfile.Name;
                Profile.AppID = serverProfile.AppID;
                Profile.InstallDirectory = serverProfile.InstallDirectory;
                Profile.Arguments = serverProfile.Arguments ?? string.Empty;
                Profile.ValidateFiles = serverProfile.ValidateFiles;
                Profile.AutoRun = serverProfile.AutoRun;
                Profile.AnonymousLogin = serverProfile.AnonymousLogin;

                if (!serverProfile.AnonymousLogin)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(serverProfile.SteamUsername))
                        {
                            _logger.LogInformation($"Giải mã username: {serverProfile.SteamUsername.Substring(0, Math.Min(10, serverProfile.SteamUsername.Length))}...");
                            NewUsername = _encryptionService.Decrypt(serverProfile.SteamUsername);
                            _logger.LogInformation($"Username đã giải mã: {(string.IsNullOrEmpty(NewUsername) ? "Trống" : "Có giá trị")}");
                        }
                        else
                        {
                            NewUsername = string.Empty;
                            _logger.LogInformation("Username từ server là rỗng");
                        }

                        if (!string.IsNullOrEmpty(serverProfile.SteamPassword))
                        {
                            _logger.LogInformation($"Giải mã password: {serverProfile.SteamPassword.Substring(0, Math.Min(10, serverProfile.SteamPassword.Length))}...");
                            NewPassword = _encryptionService.Decrypt(serverProfile.SteamPassword);
                            _logger.LogInformation($"Password đã giải mã: {(string.IsNullOrEmpty(NewPassword) ? "Trống" : "Có giá trị")}");
                        }
                        else
                        {
                            NewPassword = string.Empty;
                            _logger.LogInformation("Password từ server là rỗng");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi giải mã thông tin đăng nhập từ server");
                        StatusMessage = "Lỗi khi giải mã thông tin đăng nhập từ server: " + ex.Message;
                        IsSuccess = false;
                        await LoadServerSettingsAsync();
                        await LoadServerProfilesAsync();
                        return Page();
                    }
                }
                else
                {
                    NewUsername = string.Empty;
                    NewPassword = string.Empty;
                    _logger.LogInformation("Đăng nhập ẩn danh - bỏ qua username/password");
                }

                StatusMessage = $"Đã đồng bộ profile '{SelectedServerProfile}' từ server";
                IsSuccess = true;

                await LoadServerSettingsAsync();
                await LoadServerProfilesAsync();

                _logger.LogInformation("Hoàn tất đồng bộ profile từ server");
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profile từ server");
                StatusMessage = $"Lỗi khi đồng bộ profile: {ex.Message}";
                IsSuccess = false;
                await LoadServerSettingsAsync();
                await LoadServerProfilesAsync();
                return Page();
            }
        }
    }
}