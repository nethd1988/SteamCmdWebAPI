using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Pages
{
    public class AppUpdateViewModel
    {
        public AppUpdateInfo ApiInfo { get; set; }
        public bool NeedsUpdate { get; set; }
        public long SizeOnDisk { get; set; }
        public string FormattedSize => SizeOnDisk > 0 ? $"{(SizeOnDisk / 1024.0 / 1024.0 / 1024.0):F2} GB" : "Không xác định";

        // Thuộc tính để hiển thị dễ đọc
        public int ProfileId { get; set; }
        public string ProfileName { get; set; }
        public string Username { get; set; } // Tên tài khoản Steam đã giải mã
        public string InstallPath { get; set; } // Đường dẫn cài đặt
        public bool IsRegisteredForUpdates { get; set; } // Đã đăng ký nhận thông báo từ SteamKit hay chưa
    }

    public class UpdateManagementModel : PageModel
    {
        private readonly ILogger<UpdateManagementModel> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly UpdateCheckService _updateCheckService;
        private readonly EncryptionService _encryptionService;
        private readonly SteamIconService _steamIconService;
        private readonly IconCacheService _iconCacheService;

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public List<AppUpdateViewModel> UpdateInfos { get; set; } = new List<AppUpdateViewModel>();

        [BindProperty]
        public bool UpdateCheckEnabled { get; set; }

        [BindProperty]
        public bool AutoUpdateProfiles { get; set; }

        public UpdateManagementModel(
            ILogger<UpdateManagementModel> logger,
            SteamApiService steamApiService,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            UpdateCheckService updateCheckService,
            EncryptionService encryptionService,
            SteamIconService steamIconService,
            IconCacheService iconCacheService)
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _updateCheckService = updateCheckService;
            _encryptionService = encryptionService;
            _steamIconService = steamIconService;
            _iconCacheService = iconCacheService;
        }

        public async Task OnGetAsync()
        {
            try
            {
                var currentSettings = _updateCheckService.GetCurrentSettings();
                UpdateCheckEnabled = currentSettings.Enabled;
                AutoUpdateProfiles = currentSettings.AutoUpdateProfiles;

                var profiles = await _profileService.GetAllProfiles();
                
                // Lấy danh sách AppID đã đăng ký với Sever GL
                var registeredAppIds = await _steamApiService.GetRegisteredAppIdsAsync();
                
                // Danh sách các AppID cần đăng ký
                var appsToRegister = new List<(string appId, int profileId)>();

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
                    }
                    
                    // Kiểm tra xem AppID đã được đăng ký chưa
                    bool isRegistered = registeredAppIds.Contains(profile.AppID);
                    
                    // Nếu AppID chưa được đăng ký, thêm vào danh sách cần đăng ký
                    if (!isRegistered)
                    {
                        appsToRegister.Add((profile.AppID, profile.Id));
                    }

                    // Giải mã tên tài khoản
                    string username = "Không có";
                    if (!string.IsNullOrEmpty(profile.SteamUsername))
                    {
                        try
                        {
                            username = _encryptionService.Decrypt(profile.SteamUsername);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã tên tài khoản cho profile {0}", profile.Name);
                        }
                    }

                    // Xử lý thông tin ứng dụng
                    var apiInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID);
                    if (apiInfo == null)
                    {
                        var emptyInfo = new AppUpdateViewModel
                        {
                            ApiInfo = new AppUpdateInfo
                            {
                                AppID = profile.AppID,
                                Name = profile.Name,
                                LastUpdate = "Không thể lấy thông tin API"
                            },
                            NeedsUpdate = false,
                            ProfileId = profile.Id,
                            ProfileName = profile.Name,
                            Username = username,
                            InstallPath = profile.InstallDirectory,
                            IsRegisteredForUpdates = isRegistered
                        };
                        
                        // Thử tải icon cho game nếu chưa có
                        if (string.IsNullOrEmpty(emptyInfo.ApiInfo.IconPath))
                        {
                            emptyInfo.ApiInfo.IconPath = await _steamIconService.GetGameIconAsync(profile.AppID);
                        }
                        
                        UpdateInfos.Add(emptyInfo);
                        continue;
                    }

                    var viewModel = new AppUpdateViewModel
                    {
                        ApiInfo = apiInfo,
                        NeedsUpdate = false, // Tắt tính năng kiểm tra changenumber và ngày tháng
                        SizeOnDisk = apiInfo.SizeOnDisk,
                        ProfileId = profile.Id,
                        ProfileName = profile.Name,
                        Username = username,
                        InstallPath = profile.InstallDirectory,
                        IsRegisteredForUpdates = isRegistered
                    };

                    // Đảm bảo có icon cho game
                    if (string.IsNullOrEmpty(viewModel.ApiInfo.IconPath))
                    {
                        viewModel.ApiInfo.IconPath = await _steamIconService.GetGameIconAsync(profile.AppID);
                        
                        // Lưu lại đường dẫn icon vào cache nếu tải được
                        if (!string.IsNullOrEmpty(viewModel.ApiInfo.IconPath))
                        {
                            apiInfo.IconPath = viewModel.ApiInfo.IconPath;
                            await _steamApiService.SaveCachedAppInfo();
                        }
                    }

                    try
                    {
                        // Logic đọc manifest và cập nhật SizeOnDisk
                        if (viewModel.SizeOnDisk == 0)
                        {
                            string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                            var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                            if (manifestData != null && manifestData.TryGetValue("SizeOnDisk", out string sizeOnDiskStr) &&
                                long.TryParse(sizeOnDiskStr, out long sizeOnDisk))
                            {
                                viewModel.SizeOnDisk = sizeOnDisk;
                                await _steamApiService.UpdateSizeOnDiskFromManifest(profile.AppID, sizeOnDisk);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đọc manifest cho profile {0} (AppID: {1})", profile.Name, profile.AppID);
                    }

                    // Tắt logic kiểm tra changenumber và ngày tháng
                    // Các kiểm tra cập nhật sẽ được xử lý thông qua SteamKit notifications
                    
                    UpdateInfos.Add(viewModel);
                }

                // Sắp xếp theo tên ứng dụng
                UpdateInfos = UpdateInfos.OrderBy(vm => vm.ApiInfo.Name).ToList();
                
                // Đăng ký tự động các game chưa đăng ký
                if (appsToRegister.Count > 0)
                {
                    int registeredCount = 0;
                    _logger.LogInformation("Bắt đầu đăng ký tự động {0} game chưa đăng ký", appsToRegister.Count);
                    
                    foreach (var (appId, profileId) in appsToRegister)
                    {
                        try
                        {
                            bool success = await _steamApiService.RegisterForAppUpdates(appId, profileId);
                            if (success)
                            {
                                registeredCount++;
                                
                                // Cập nhật trạng thái đăng ký trong danh sách hiển thị
                                var viewModel = UpdateInfos.FirstOrDefault(vm => vm.ApiInfo.AppID == appId);
                                if (viewModel != null)
                                {
                                    viewModel.IsRegisteredForUpdates = true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi tự động đăng ký cập nhật cho AppID {0}", appId);
                        }
                    }
                    
                    if (registeredCount > 0)
                    {
                        StatusMessage = $"Đã tự động đăng ký {registeredCount} game để nhận thông báo cập nhật.";
                        IsSuccess = true;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải thông tin cập nhật hoặc cài đặt");
                StatusMessage = $"Lỗi khi tải thông tin: {ex.Message}";
                IsSuccess = false;
            }
        }

        public async Task<IActionResult> OnPostSaveUpdateCheckSettingsAsync()
        {
            try
            {
                // Mặc định thời gian kiểm tra là 10 phút
                int updateIntervalMinutes = 10;

                _updateCheckService.UpdateSettings(
                    UpdateCheckEnabled,
                    TimeSpan.FromMinutes(updateIntervalMinutes),
                    AutoUpdateProfiles,
                    true); // Luôn sử dụng Sever GL

                // Đăng ký nhận thông báo cập nhật từ Sever GL nếu tính năng được bật
                if (UpdateCheckEnabled)
                {
                    try
                    {
                        var profiles = await _profileService.GetAllProfiles();
                        int registered = 0;
                        int total = 0;
                        
                        foreach (var profile in profiles)
                        {
                            if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                            {
                                continue;
                            }
                            
                            total++;
                            // Đăng ký theo dõi cập nhật cho appID
                            bool success = await _steamApiService.RegisterForAppUpdates(profile.AppID, profile.Id);
                            if (success)
                            {
                                registered++;
                            }
                        }
                        
                        StatusMessage = $"Cài đặt kiểm tra cập nhật đã được lưu thành công. Đã đăng ký {registered}/{total} ứng dụng nhận thông báo từ Sever GL.";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể đăng ký nhận thông báo cập nhật tự động");
                        StatusMessage = $"Cài đặt đã được lưu, nhưng có lỗi khi đăng ký nhận thông báo: {ex.Message}";
                    }
                }
                else
                {
                    StatusMessage = $"Cài đặt kiểm tra cập nhật đã được lưu thành công. {(UpdateCheckEnabled ? "Chức năng đã được bật" : "Chức năng đã bị tắt")}, tự động cập nhật khi phát hiện: {(AutoUpdateProfiles ? "Bật" : "Tắt")}";
                }
                
                IsSuccess = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật cài đặt");
                StatusMessage = $"Lỗi khi cập nhật cài đặt: {ex.Message}";
                IsSuccess = false;
            }

            await OnGetAsync();
            return Page();
        }
        
        public async Task<IActionResult> OnPostUpdateAppAsync(string appId, int profileId)
        {
            try
            {
                // Lấy thông tin profile
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    StatusMessage = "Không tìm thấy profile.";
                    IsSuccess = false;
                    await OnGetAsync();
                    return Page();
                }

                // Lấy thông tin app từ API
                var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                if (appInfo == null)
                {
                    StatusMessage = $"Không tìm thấy thông tin cho AppID {appId}.";
                    IsSuccess = false;
                    await OnGetAsync();
                    return Page();
                }

                // Thêm vào hàng đợi cập nhật
                bool success = await _steamCmdService.RunSpecificAppAsync(profileId, appId);

                if (success)
                {
                    StatusMessage = $"Đã thêm {appInfo.Name} vào hàng đợi cập nhật thành công.";
                    IsSuccess = true;
                }
                else
                {
                    StatusMessage = $"Không thể thêm {appInfo.Name} vào hàng đợi cập nhật.";
                    IsSuccess = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật ứng dụng");
                StatusMessage = $"Lỗi khi thêm vào hàng đợi: {ex.Message}";
                IsSuccess = false;
            }

            await OnGetAsync();
            return Page();
        }

        // Thêm lớp mới cho dữ liệu trả về từ API
        public class ProfileSelectionModel
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string SteamAccount { get; set; } // Tên tài khoản Steam
            public bool HasSteamAccount => !string.IsNullOrEmpty(SteamAccount);
            public string InstallDirectory { get; set; } // Thư mục cài đặt
        }

        // Hàm lấy danh sách profile cho việc chọn
        public async Task<IActionResult> OnGetProfilesForSelectionAsync()
        {
            try
            {
                var profiles = await _profileService.GetAllProfiles();
                
                // Lấy thông tin profile chi tiết hơn
                var profileModels = new List<ProfileSelectionModel>();
                foreach (var profile in profiles)
                {
                    string steamAccount = "Không có";
                    
                    // Giải mã tên tài khoản nếu có
                    if (!string.IsNullOrEmpty(profile.SteamUsername))
                    {
                        try
                        {
                            steamAccount = _encryptionService.Decrypt(profile.SteamUsername);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã username cho profile {0}", profile.Name);
                        }
                    }
                    
                    profileModels.Add(new ProfileSelectionModel
                    {
                        Id = profile.Id,
                        Name = profile.Name,
                        SteamAccount = steamAccount,
                        InstallDirectory = !string.IsNullOrEmpty(profile.InstallDirectory) 
                            ? profile.InstallDirectory 
                            : "Chưa cấu hình"
                    });
                }
                
                return new JsonResult(new { success = true, profiles = profileModels });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profiles cho selection");
                return new JsonResult(new { success = false, message = "Lỗi khi lấy danh sách profiles: " + ex.Message });
            }
        }
        
        // Hàm thay đổi profile cho game
        public async Task<IActionResult> OnPostChangeGameProfileAsync(string appId, int profileId)
        {
            try
            {
                // Kiểm tra profile tồn tại
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    return new JsonResult(new { success = false, message = "Profile không tồn tại" });
                }
                
                // Tìm profile hiện tại đang sử dụng appId này
                var currentProfiles = await _profileService.GetProfilesByAppId(appId);
                if (currentProfiles != null && currentProfiles.Any())
                {
                    foreach (var currentProfile in currentProfiles)
                    {
                        // Tạo danh sách AppIDs mới không bao gồm appId hiện tại
                        var appIds = currentProfile.AppID.Split(',').ToList();
                        var dependencyIds = currentProfile.DependencyIDs?.Split(',').ToList() ?? new List<string>();
                        
                        if (appIds.Contains(appId))
                        {
                            appIds.Remove(appId);
                            currentProfile.AppID = string.Join(",", appIds);
                            
                            // Cập nhật profile hiện tại nếu vẫn còn AppIDs khác
                            await _profileService.UpdateProfile(currentProfile);
                        }
                    }
                }
                
                // Thêm AppID vào profile mới
                var currentAppIds = string.IsNullOrEmpty(profile.AppID) 
                    ? new List<string>() 
                    : profile.AppID.Split(',').ToList();
                
                if (!currentAppIds.Contains(appId))
                {
                    currentAppIds.Add(appId);
                    profile.AppID = string.Join(",", currentAppIds);
                    
                    // Cập nhật profile mới
                    await _profileService.UpdateProfile(profile);
                }
                
                // Cập nhật cache thông tin cập nhật
                await _steamCmdService.InvalidateAppUpdateCache(appId);
                
                return new JsonResult(new { success = true, message = "Đã thay đổi profile thành công" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thay đổi profile cho game: {AppId}", appId);
                return new JsonResult(new { success = false, message = "Lỗi khi thay đổi profile: " + ex.Message });
            }
        }

        // Thêm handler xóa profile
        public async Task<IActionResult> OnPostDeleteProfileAsync(int profileId)
        {
            try
            {
                // Lấy profile trước khi xóa để hiển thị thông báo
                var profileToDelete = await _profileService.GetProfileById(profileId);
                if (profileToDelete == null)
                {
                    StatusMessage = "Không tìm thấy profile để xóa.";
                    IsSuccess = false;
                    return RedirectToPage();
                }

                // Xóa profile
                await _profileService.DeleteProfile(profileId);

                // Cập nhật thông báo thành công
                StatusMessage = $"Đã xóa profile '{profileToDelete.Name}' thành công.";
                IsSuccess = true;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa profile {ProfileId}", profileId);
                StatusMessage = $"Lỗi khi xóa profile: {ex.Message}";
                IsSuccess = false;
                return RedirectToPage();
            }
        }

        // Handler xóa profile thông qua GET
        public async Task<IActionResult> OnGetDeleteProfileAsync(int id)
        {
            try
            {
                // Lấy profile trước khi xóa để hiển thị thông báo
                var profileToDelete = await _profileService.GetProfileById(id);
                if (profileToDelete == null)
                {
                    StatusMessage = "Không tìm thấy profile để xóa.";
                    IsSuccess = false;
                    return RedirectToPage();
                }

                // Xóa profile
                await _profileService.DeleteProfile(id);

                // Cập nhật thông báo thành công
                StatusMessage = $"Đã xóa profile '{profileToDelete.Name}' thành công.";
                IsSuccess = true;
                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa profile {ProfileId}", id);
                StatusMessage = $"Lỗi khi xóa profile: {ex.Message}";
                IsSuccess = false;
                return RedirectToPage();
            }
        }
    }
}