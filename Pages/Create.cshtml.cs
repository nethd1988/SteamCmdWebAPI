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
        private readonly ServerSettingsService _serverSettingsService;
        private readonly TcpClientService _tcpClientService;
        private readonly ILogger<CreateModel> _logger;

        [BindProperty]
        public SteamCmdProfile Profile { get; set; } = new SteamCmdProfile();

        [BindProperty]
        public string Username { get; set; } = string.Empty;

        [BindProperty]
        public string Password { get; set; } = string.Empty;

        [BindProperty]
        public string SelectedServerProfile { get; set; } = string.Empty;

        public List<SelectListItem> ServerProfiles { get; set; } = new List<SelectListItem>();

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public bool IsServerConfigured { get; set; }
        public string ServerAddress { get; set; }

        public CreateModel(
            ProfileService profileService,
            EncryptionService encryptionService,
            ServerSettingsService serverSettingsService,
            TcpClientService tcpClientService,
            ILogger<CreateModel> logger)
        {
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _serverSettingsService = serverSettingsService ?? throw new ArgumentNullException(nameof(serverSettingsService));
            _tcpClientService = tcpClientService ?? throw new ArgumentNullException(nameof(tcpClientService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Xóa StatusMessage để tránh hiển thị thông báo lỗi cũ
            StatusMessage = null;
            IsSuccess = false;

            await LoadServerSettingsAsync();
            if (IsServerConfigured)
            {
                await LoadServerProfilesAsync();
            }
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
            try
            {
                ServerProfiles.Clear();
                ServerProfiles.Add(new SelectListItem
                {
                    Value = "",
                    Text = "-- Chọn profile từ server --",
                    Selected = true
                });
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                var profileNames = await _tcpClientService.GetProfileNamesAsync(serverSettings.ServerAddress, serverSettings.ServerPort);
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

            // Loại bỏ các trường không cần thiết khỏi ModelState
            ModelState.Remove("Profile.Arguments");
            ModelState.Remove("SelectedServerProfile");
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
                await LoadServerSettingsAsync();
                if (IsServerConfigured)
                {
                    await LoadServerProfilesAsync();
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
                        await LoadServerSettingsAsync();
                        if (IsServerConfigured)
                        {
                            await LoadServerProfilesAsync();
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
                await LoadServerSettingsAsync();
                if (IsServerConfigured)
                {
                    await LoadServerProfilesAsync();
                }
                return Page();
            }
        }

        public async Task<IActionResult> OnPostLoadServerProfileAsync()
        {
            _logger.LogInformation("OnPostLoadServerProfileAsync called");

            try
            {
                _logger.LogInformation($"Bắt đầu tải profile từ server: {SelectedServerProfile}");

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

                // Điền thông tin vào form
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
                            Username = _encryptionService.Decrypt(serverProfile.SteamUsername);
                            _logger.LogInformation($"Username đã giải mã: {(string.IsNullOrEmpty(Username) ? "Trống" : "Có giá trị")}");
                        }
                        else
                        {
                            Username = string.Empty;
                            _logger.LogInformation("Username từ server là rỗng");
                        }

                        if (!string.IsNullOrEmpty(serverProfile.SteamPassword))
                        {
                            _logger.LogInformation($"Giải mã password: {serverProfile.SteamPassword.Substring(0, Math.Min(10, serverProfile.SteamPassword.Length))}...");
                            Password = _encryptionService.Decrypt(serverProfile.SteamPassword);
                            _logger.LogInformation($"Password đã giải mã: {(string.IsNullOrEmpty(Password) ? "Trống" : "Có giá trị")}");
                        }
                        else
                        {
                            Password = string.Empty;
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
                    Username = string.Empty;
                    Password = string.Empty;
                    _logger.LogInformation("Đăng nhập ẩn danh - bỏ qua username/password");
                }

                StatusMessage = $"Đã tải profile '{SelectedServerProfile}' từ server";
                IsSuccess = true;

                await LoadServerSettingsAsync();
                await LoadServerProfilesAsync();

                _logger.LogInformation("Hoàn tất tải profile từ server");
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải profile từ server");
                StatusMessage = $"Lỗi khi tải profile: {ex.Message}";
                IsSuccess = false;
                await LoadServerSettingsAsync();
                await LoadServerProfilesAsync();
                return Page();
            }
        }
    }
}