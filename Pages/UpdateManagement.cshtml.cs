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
using Newtonsoft.Json;

namespace SteamCmdWebAPI.Pages
{
    public class AppUpdateViewModel
    {
        public AppUpdateInfo ApiInfo { get; set; }
        public bool NeedsUpdate { get; set; }
        public bool HasChangedSinceLastCheck { get; set; }
        public long SizeOnDisk { get; set; }
        public string LastUpdateStatus { get; set; }
        public string FormattedSize => SizeOnDisk > 0 ? $"{(SizeOnDisk / 1024.0 / 1024.0 / 1024.0):F2} GB" : "Không xác định";
        public long PreviousApiChangeNumber => ApiInfo?.LastCheckedChangeNumber ?? 0;
        public long CurrentApiChangeNumber => ApiInfo?.ChangeNumber ?? 0;
        public DateTime? PreviousUpdateDateTime => ApiInfo?.LastCheckedUpdateDateTime;
        public DateTime? CurrentUpdateDateTime => ApiInfo?.LastUpdateDateTime;

        // Thêm các thuộc tính mới [cite: 2, 3, 4, 5]
        public int ProfileId { get; set; }
        public string ProfileName { get; set; }
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
        // Thêm DependencyManagerService [cite: 6]
        private readonly DependencyManagerService _dependencyManagerService;


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
            // Inject DependencyManagerService [cite: 9]
            DependencyManagerService dependencyManagerService)
        {
            _logger = logger; // [cite: 7]
            _steamApiService = steamApiService; // [cite: 7]
            _profileService = profileService; // [cite: 8]
            _steamCmdService = steamCmdService; // [cite: 8]
            _updateCheckService = updateCheckService; // [cite: 9]
            _dependencyManagerService = dependencyManagerService; // [cite: 9]
        }

        public async Task OnGetAsync()
        {
            try
            {
                var currentSettings = _updateCheckService.GetCurrentSettings(); // [cite: 10]
                UpdateCheckEnabled = currentSettings.Enabled; // [cite: 11]
                UpdateCheckIntervalMinutes = currentSettings.IntervalMinutes; // [cite: 12]
                AutoUpdateProfiles = currentSettings.AutoUpdateProfiles; // [cite: 13]

                if (UpdateCheckIntervalMinutes < 10) UpdateCheckIntervalMinutes = 10; // [cite: 14]
                if (UpdateCheckIntervalMinutes > 1440) UpdateCheckIntervalMinutes = 1440; // [cite: 15]

                var profiles = await _profileService.GetAllProfiles(); // [cite: 16]
                // Lấy tất cả phụ thuộc [cite: 17]
                var allDependencies = await _dependencyManagerService.GetAllDependenciesAsync(); // [cite: 17]

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} do thiếu AppID hoặc Thư mục cài đặt.", profile.Name); // [cite: 18]
                        continue; // [cite: 19]
                    }

                    // Xử lý app chính
                    var apiInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID); // [cite: 20]
                    if (apiInfo == null)
                    {
                        UpdateInfos.Add(new AppUpdateViewModel
                        {
                            ApiInfo = new AppUpdateInfo // [cite: 21]
                            {
                                AppID = profile.AppID, // [cite: 21]
                                Name = profile.Name, // [cite: 21]
                                LastUpdate = "Không thể lấy thông tin API" // [cite: 22]
                            },
                            NeedsUpdate = false, // [cite: 22]
                            LastUpdateStatus = "Không xác định", // [cite: 23]
                            ProfileId = profile.Id, // [cite: 23]
                            ProfileName = profile.Name, // [cite: 23]
                            IsMainApp = true // [cite: 23]
                        });
                        continue; // [cite: 24]
                    }

                    var viewModel = new AppUpdateViewModel
                    {
                        ApiInfo = apiInfo, // [cite: 25]
                        NeedsUpdate = false, // [cite: 26]
                        HasChangedSinceLastCheck = false, // [cite: 26]
                        SizeOnDisk = apiInfo.SizeOnDisk, // [cite: 26]
                        LastUpdateStatus = "Đã cập nhật", // [cite: 27]
                        ProfileId = profile.Id, // [cite: 27]
                        ProfileName = profile.Name, // [cite: 27]
                        IsMainApp = true // [cite: 27]
                    };

                    try
                    {
                        // Logic đọc manifest và cập nhật SizeOnDisk vẫn giữ nguyên
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

                    // Logic kiểm tra NeedsUpdate và HasChangedSinceLastCheck vẫn giữ nguyên
                    if (apiInfo.LastCheckedChangeNumber > 0 && apiInfo.ChangeNumber != apiInfo.LastCheckedChangeNumber)
                    {
                        viewModel.HasChangedSinceLastCheck = true;
                        viewModel.LastUpdateStatus = "ChangeNumber thay đổi từ lần kiểm tra trước";
                        viewModel.NeedsUpdate = true;
                    }
                    else if (apiInfo.LastCheckedUpdateDateTime.HasValue &&
                            apiInfo.LastUpdateDateTime.HasValue &&
                            apiInfo.LastUpdateDateTime.Value != apiInfo.LastCheckedUpdateDateTime.Value)
                    {
                        viewModel.HasChangedSinceLastCheck = true;
                        viewModel.LastUpdateStatus = "Thời gian cập nhật thay đổi từ lần kiểm tra trước";
                        viewModel.NeedsUpdate = true;
                    }


                    UpdateInfos.Add(viewModel); // [cite: 29]

                    // Xử lý các ứng dụng phụ thuộc [cite: 30]
                    var dependency = allDependencies.FirstOrDefault(d => d.ProfileId == profile.Id); // [cite: 30]
                    if (dependency != null && dependency.DependentApps.Any()) // [cite: 30]
                    {
                        foreach (var app in dependency.DependentApps) // [cite: 31]
                        {
                            var dependentAppInfo = await _steamApiService.GetAppUpdateInfo(app.AppId); // [cite: 31]
                            if (dependentAppInfo == null) continue; // [cite: 31]

                            var dependentViewModel = new AppUpdateViewModel // [cite: 32]
                            {
                                ApiInfo = dependentAppInfo, // [cite: 33]
                                // Lấy trạng thái NeedsUpdate từ thông tin dependency [cite: 33]
                                NeedsUpdate = app.NeedsUpdate, // [cite: 33]
                                HasChangedSinceLastCheck = app.NeedsUpdate, // [cite: 33]
                                SizeOnDisk = dependentAppInfo.SizeOnDisk, // [cite: 34]
                                LastUpdateStatus = app.NeedsUpdate ? "Cần cập nhật (Phụ thuộc)" : "Đã cập nhật (Phụ thuộc)", // [cite: 34, 35]
                                ProfileId = profile.Id, // [cite: 36]
                                ProfileName = profile.Name, // [cite: 36]
                                IsMainApp = false, // [cite: 36]
                                ParentAppId = profile.AppID // [cite: 36]
                            };

                            // Cập nhật SizeOnDisk cho app phụ thuộc từ manifest (tương tự app chính)
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

                            UpdateInfos.Add(dependentViewModel); // [cite: 38]
                        }
                    }
                }

                // Sắp xếp: App chính lên đầu, sau đó theo thời gian cập nhật [cite: 39]
                UpdateInfos = UpdateInfos
                    .OrderByDescending(vm => vm.IsMainApp) // [cite: 39]
                    .ThenByDescending(vm => vm.CurrentUpdateDateTime) // [cite: 39]
                    .ToList(); // [cite: 39]
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải thông tin cập nhật hoặc cài đặt"); // [cite: 40]
                StatusMessage = $"Lỗi khi tải thông tin: {ex.Message}"; // [cite: 41]
                IsSuccess = false; // [cite: 42]
            }
        }

        public async Task<IActionResult> OnPostSaveUpdateCheckSettingsAsync()
        {
            // Logic này giữ nguyên, không thay đổi theo hướng dẫn
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

                        string updateReason = $"ChangeNumber API thay đổi: {previousChangeNumber} -> {latestAppInfo.ChangeNumber}";
                        _logger.LogInformation("Phát hiện thay đổi ChangeNumber cho app chính {0} (AppID: {1}): {2} -> {3}",
                            profile.Name, profile.AppID, previousChangeNumber, latestAppInfo.ChangeNumber);

                        // Chỉ cập nhật app chính nếu có cập nhật
                        if (mainAppNeedsUpdate)
                        {
                            _logger.LogInformation("Thêm app chính {0} (AppID: {1}) vào hàng đợi cập nhật...",
                                profile.Name, profile.AppID);

                            // Chỉ cập nhật app chính
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

                                string updateReason = $"ChangeNumber API thay đổi: {depPreviousChangeNumber} -> {depLatestInfo.ChangeNumber}";
                                _logger.LogInformation("Phát hiện thay đổi ChangeNumber cho app phụ thuộc (AppID: {0}): {1}",
                                    appId, updateReason);

                                // Đánh dấu app cần cập nhật
                                await _dependencyManagerService.MarkAppForUpdateAsync(appId);

                                // Chỉ cập nhật app phụ thuộc cụ thể nếu có cập nhật
                                if (depNeedsUpdate)
                                {
                                    _logger.LogInformation("Thêm app phụ thuộc (AppID: {0}) vào hàng đợi cập nhật...", appId);

                                    // Chạy riêng cho từng app phụ thuộc
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
            // Logic này giữ nguyên, không thay đổi theo hướng dẫn
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

        // Cập nhật phương thức để xử lý app chính và phụ thuộc [cite: 43]
        public async Task<IActionResult> OnPostUpdateAppAsync(string appId, int profileId, bool isMainApp) // Thêm profileId và isMainApp
        {
            try
            {
                if (string.IsNullOrEmpty(appId))
                {
                    return new JsonResult(new { success = false, message = "Không có App ID được chỉ định." }); // [cite: 43]
                }

                bool success; // [cite: 44]

                if (isMainApp) // [cite: 44]
                {
                    // Nếu là app chính, cập nhật toàn bộ profile [cite: 45]
                    _logger.LogInformation("Yêu cầu cập nhật profile chính: ProfileId={ProfileId}, AppId={AppId}", profileId, appId);
                    success = await _steamCmdService.QueueProfileForUpdate(profileId); // [cite: 45]

                    return new JsonResult(new
                    {
                        success = success, // [cite: 46]
                        message = success // [cite: 46]
                            ? $"Đã thêm profile (ID: {profileId}) với App ID {appId} vào hàng đợi cập nhật." // [cite: 46]
                            : $"Không thể thêm profile (ID: {profileId}) vào hàng đợi." // [cite: 46]
                    }); // [cite: 47]
                }
                else
                {
                    // Nếu là app phụ thuộc, chỉ cập nhật app đó [cite: 48]
                    _logger.LogInformation("Yêu cầu cập nhật app phụ thuộc: ProfileId={ProfileId}, AppId={AppId}", profileId, appId);
                    success = await _steamCmdService.RunSpecificAppAsync(profileId, appId); // [cite: 48]

                    return new JsonResult(new
                    {
                        success = success, // [cite: 49]
                        message = success // [cite: 49]
                            ? $"Đã thêm App ID {appId} (phụ thuộc) của profile ID {profileId} vào hàng đợi cập nhật." // [cite: 49]
                            : $"Không thể cập nhật App ID {appId} (phụ thuộc)." // [cite: 49]
                    }); // [cite: 50]
                }
            }
            catch (Exception ex)
            {
                // Cập nhật logging để bao gồm cả profileId [cite: 51]
                _logger.LogError(ex, "Lỗi khi cập nhật App ID {AppId} của profile {ProfileId}", appId, profileId); // [cite: 51]
                return new JsonResult(new { success = false, message = ex.Message }); // [cite: 52]
            }
        }
    }
}