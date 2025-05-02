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
        public bool IsMainApp { get; set; } = true;
        public string ParentAppId { get; set; } // AppId chính nếu đây là app phụ thuộc
    }

    public class UpdateManagementModel : PageModel
    {
        private readonly ILogger<UpdateManagementModel> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly UpdateCheckService _updateCheckService;
        private readonly DependencyManagerService _dependencyManagerService;
        private readonly EncryptionService _encryptionService;

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public List<AppUpdateViewModel> UpdateInfos { get; set; } = new List<AppUpdateViewModel>();

        [BindProperty]
        public bool UpdateCheckEnabled { get; set; }

        [BindProperty]
        public int UpdateCheckIntervalMinutes { get; set; } = 60;

        [BindProperty]
        public bool AutoUpdateProfiles { get; set; }

        public UpdateManagementModel(
            ILogger<UpdateManagementModel> logger,
            SteamApiService steamApiService,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            UpdateCheckService updateCheckService,
            DependencyManagerService dependencyManagerService,
            EncryptionService encryptionService)
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _updateCheckService = updateCheckService;
            _dependencyManagerService = dependencyManagerService;
            _encryptionService = encryptionService;
        }

        public async Task OnGetAsync()
        {
            try
            {
                var currentSettings = _updateCheckService.GetCurrentSettings();
                UpdateCheckEnabled = currentSettings.Enabled;
                UpdateCheckIntervalMinutes = currentSettings.IntervalMinutes;
                AutoUpdateProfiles = currentSettings.AutoUpdateProfiles;

                if (UpdateCheckIntervalMinutes < 10) UpdateCheckIntervalMinutes = 10;
                if (UpdateCheckIntervalMinutes > 1440) UpdateCheckIntervalMinutes = 1440;

                var profiles = await _profileService.GetAllProfiles();
                var allDependencies = await _dependencyManagerService.GetAllDependenciesAsync();

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
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

                    // Xử lý app chính
                    var apiInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID);
                    if (apiInfo == null)
                    {
                        UpdateInfos.Add(new AppUpdateViewModel
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
                            IsMainApp = true
                        });
                        continue;
                    }

                    var viewModel = new AppUpdateViewModel
                    {
                        ApiInfo = apiInfo,
                        NeedsUpdate = false,
                        SizeOnDisk = apiInfo.SizeOnDisk,
                        ProfileId = profile.Id,
                        ProfileName = profile.Name,
                        Username = username,
                        IsMainApp = true
                    };

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

                    // Đơn giản hóa việc xác định cần cập nhật hay không
                    if (apiInfo.LastCheckedChangeNumber > 0 && apiInfo.ChangeNumber != apiInfo.LastCheckedChangeNumber)
                    {
                        viewModel.NeedsUpdate = true;
                    }
                    else if (apiInfo.LastCheckedUpdateDateTime.HasValue &&
                            apiInfo.LastUpdateDateTime.HasValue &&
                            apiInfo.LastUpdateDateTime.Value != apiInfo.LastCheckedUpdateDateTime.Value)
                    {
                        viewModel.NeedsUpdate = true;
                    }

                    UpdateInfos.Add(viewModel);

                    // Xử lý các ứng dụng phụ thuộc
                    var dependency = allDependencies.FirstOrDefault(d => d.ProfileId == profile.Id);
                    if (dependency != null && dependency.DependentApps.Any())
                    {
                        foreach (var app in dependency.DependentApps)
                        {
                            var dependentAppInfo = await _steamApiService.GetAppUpdateInfo(app.AppId);
                            if (dependentAppInfo == null) continue;

                            var dependentViewModel = new AppUpdateViewModel
                            {
                                ApiInfo = dependentAppInfo,
                                NeedsUpdate = app.NeedsUpdate,
                                SizeOnDisk = dependentAppInfo.SizeOnDisk,
                                ProfileId = profile.Id,
                                ProfileName = profile.Name,
                                Username = username, // Sử dụng cùng tài khoản với profile chính
                                IsMainApp = false,
                                ParentAppId = profile.AppID
                            };

                            // Cập nhật SizeOnDisk cho app phụ thuộc từ manifest
                            try
                            {
                                if (dependentViewModel.SizeOnDisk == 0)
                                {
                                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                                    var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, app.AppId);
                                    if (manifestData != null && manifestData.TryGetValue("SizeOnDisk", out string sizeOnDiskStr) &&
                                        long.TryParse(sizeOnDiskStr, out long sizeOnDisk))
                                    {
                                        dependentViewModel.SizeOnDisk = sizeOnDisk;
                                        await _steamApiService.UpdateSizeOnDiskFromManifest(app.AppId, sizeOnDisk);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Lỗi khi đọc manifest cho app phụ thuộc {0} (Profile: {1})", app.AppId, profile.Name);
                            }

                            UpdateInfos.Add(dependentViewModel);
                        }
                    }
                }

                // Sắp xếp: App chính lên đầu, sau đó theo tên app
                UpdateInfos = UpdateInfos
                    .OrderByDescending(vm => vm.IsMainApp)
                    .ThenBy(vm => vm.ApiInfo.Name)
                    .ToList();
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
                if (UpdateCheckIntervalMinutes < 10 || UpdateCheckIntervalMinutes > 1440)
                {
                    StatusMessage = "Khoảng thời gian kiểm tra (phút) phải từ 10 đến 1440.";
                    IsSuccess = false;
                    await OnGetAsync();
                    return Page();
                }

                _updateCheckService.UpdateSettings(
                    UpdateCheckEnabled,
                    TimeSpan.FromMinutes(UpdateCheckIntervalMinutes),
                    AutoUpdateProfiles
                );

                StatusMessage = $"Đã lưu cài đặt kiểm tra cập nhật: {(UpdateCheckEnabled ? "Bật" : "Tắt")}, " +
                                $"{UpdateCheckIntervalMinutes} phút/lần, Tự động cập nhật: {(AutoUpdateProfiles ? "Bật" : "Tắt")}.";
                IsSuccess = true;

                await OnGetAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật");
                StatusMessage = $"Lỗi khi lưu cài đặt: {ex.Message}";
                IsSuccess = false;
                await OnGetAsync();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostCheckUpdatesAsync()
        {
            try
            {
                var profiles = await _profileService.GetAllProfiles();
                int checkedCount = 0;
                int profilesNeedingUpdateCount = 0;
                int appsNeedingUpdateCount = 0;

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} trong kiểm tra thủ công do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
                    }

                    checkedCount++;
                    bool profileNeedsUpdate = false;
                    _logger.LogInformation("Kiểm tra cập nhật thủ công cho profile: {0} (AppID: {1})", profile.Name, profile.AppID);

                    // Kiểm tra app chính
                    var cachedAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: false);
                    long previousChangeNumber = cachedAppInfo?.LastCheckedChangeNumber ?? 0;

                    var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true);
                    if (latestAppInfo == null)
                    {
                        _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID {1} ({0}) trong kiểm tra thủ công.",
                            profile.Name, profile.AppID);
                        continue;
                    }

                    // Kiểm tra cập nhật cho app chính
                    bool mainAppNeedsUpdate = false;
                    if (previousChangeNumber > 0 && latestAppInfo.ChangeNumber != previousChangeNumber)
                    {
                        mainAppNeedsUpdate = true;
                        profileNeedsUpdate = true;
                        appsNeedingUpdateCount++;

                        // Chỉ cập nhật app chính nếu có cập nhật
                        if (mainAppNeedsUpdate)
                        {
                            await _steamCmdService.RunSpecificAppAsync(profile.Id, profile.AppID);
                        }
                    }

                    // Cập nhật SizeOnDisk từ manifest
                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                    var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                    if (manifestData != null && manifestData.TryGetValue("SizeOnDisk", out string sizeOnDiskStr) &&
                        long.TryParse(sizeOnDiskStr, out long sizeOnDisk))
                    {
                        await _steamApiService.UpdateSizeOnDiskFromManifest(profile.AppID, sizeOnDisk);
                    }

                    // Kiểm tra các app phụ thuộc
                    try
                    {
                        var dependentAppIds = await _dependencyManagerService.ScanDependenciesFromManifest(steamappsDir, profile.AppID);

                        // Cập nhật danh sách phụ thuộc vào cơ sở dữ liệu
                        await _dependencyManagerService.UpdateDependenciesAsync(profile.Id, profile.AppID, dependentAppIds);

                        foreach (var appId in dependentAppIds)
                        {
                            var depCachedInfo = await _steamApiService.GetAppUpdateInfo(appId, forceRefresh: false);
                            long depPreviousChangeNumber = depCachedInfo?.LastCheckedChangeNumber ?? 0;

                            var depLatestInfo = await _steamApiService.GetAppUpdateInfo(appId, forceRefresh: true);
                            if (depLatestInfo == null) continue;

                            bool depNeedsUpdate = false;
                            if (depPreviousChangeNumber > 0 && depLatestInfo.ChangeNumber != depPreviousChangeNumber)
                            {
                                depNeedsUpdate = true;
                                profileNeedsUpdate = true;
                                appsNeedingUpdateCount++;

                                // Đánh dấu app cần cập nhật
                                await _dependencyManagerService.MarkAppForUpdateAsync(appId);

                                // Chỉ cập nhật app phụ thuộc cụ thể nếu có cập nhật
                                if (depNeedsUpdate)
                                {
                                    await _steamCmdService.RunSpecificAppAsync(profile.Id, appId);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi kiểm tra các app phụ thuộc của profile '{0}'", profile.Name);
                    }

                    if (profileNeedsUpdate)
                    {
                        profilesNeedingUpdateCount++;
                    }
                }

                await _steamApiService.SaveCachedAppInfo();

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã kiểm tra {checkedCount} profile. Phát hiện {profilesNeedingUpdateCount} profile có {appsNeedingUpdateCount} app cần cập nhật. " +
                            "Các app cần cập nhật đã được thêm vào hàng đợi."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra cập nhật thủ công");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostClearCacheAsync()
        {
            try
            {
                await _steamApiService.ClearCacheAsync();
                _logger.LogInformation("Đã xóa cache thông tin cập nhật");

                return new JsonResult(new { success = true, message = "Đã xóa cache thông tin cập nhật." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa cache cập nhật");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostUpdateAppAsync(string appId, int profileId, bool isMainApp)
        {
            try
            {
                if (string.IsNullOrEmpty(appId))
                {
                    return new JsonResult(new { success = false, message = "Không có App ID được chỉ định." });
                }

                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy profile." });
                }

                // Sử dụng SteamCmdService trực tiếp 
                bool success = await _steamCmdService.RunSpecificAppAsync(profileId, appId);

                if (success)
                {
                    string message = isMainApp
                        ? $"Đã thêm profile '{profile.Name}' với App ID {appId} vào hàng đợi cập nhật."
                        : $"Đã thêm App ID {appId} (phụ thuộc) của profile '{profile.Name}' vào hàng đợi cập nhật.";

                    return new JsonResult(new { success = true, message = message });
                }
                else
                {
                    string message = isMainApp
                        ? $"Không thể thêm profile '{profile.Name}' vào hàng đợi."
                        : $"Không thể thêm App ID {appId} (phụ thuộc) vào hàng đợi.";

                    return new JsonResult(new { success = false, message = message });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật App ID {AppId} của profile {ProfileId}", appId, profileId);
                return new JsonResult(new { success = false, message = ex.Message });
            }
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
    }
}