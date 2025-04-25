using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SteamCmdWebAPI.Services;
using SteamCmdWebAPI.Models;
using Newtonsoft.Json;

namespace SteamCmdWebAPI.Pages
{
    // ViewModel to combine API info with local update status
    public class AppUpdateViewModel
    {
        public AppUpdateInfo ApiInfo { get; set; }
        public bool NeedsUpdate { get; set; }
        public long LocalChangeNumber { get; set; } = -1; // Add local change number for display
    }

    public class UpdateManagementModel : PageModel
    {
        private readonly ILogger<UpdateManagementModel> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly UpdateCheckService _updateCheckService; // Inject UpdateCheckService directly

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        // Change list type to the new ViewModel
        public List<AppUpdateViewModel> UpdateInfos { get; set; } = new List<AppUpdateViewModel>();

        // Properties for Auto Update Check Settings - Match SettingsPageModel
        [BindProperty]
        public bool UpdateCheckEnabled { get; set; }
        [BindProperty]
        public int UpdateCheckIntervalMinutes { get; set; } = 60; // Default to match SettingsPageModel
        [BindProperty] // Keep this property for the checkbox
        public bool AutoUpdateProfiles { get; set; }


        public UpdateManagementModel(
            ILogger<UpdateManagementModel> logger,
            SteamApiService steamApiService,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            UpdateCheckService updateCheckService) // Inject UpdateCheckService
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _updateCheckService = updateCheckService; // Assign injected service

            // Removed file path and file loading/saving logic from here,
            // as UpdateCheckService is the source of truth.
        }

        public async Task OnGetAsync()
        {
            try
            {
                // Load settings from the service
                var currentSettings = _updateCheckService.GetCurrentSettings();
                UpdateCheckEnabled = currentSettings.Enabled;
                UpdateCheckIntervalMinutes = currentSettings.IntervalMinutes;
                AutoUpdateProfiles = currentSettings.AutoUpdateProfiles; // Load AutoUpdateProfiles setting

                // Ensure interval is within reasonable bounds for the UI
                if (UpdateCheckIntervalMinutes < 10) UpdateCheckIntervalMinutes = 10;
                if (UpdateCheckIntervalMinutes > 1440) UpdateCheckIntervalMinutes = 1440;


                var profiles = await _profileService.GetAllProfiles();

                // Process each profile to get API info and local status
                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
                    }

                    // 1. Get API Info
                    var apiInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID);

                    if (apiInfo != null)
                    {
                        long localChangeNumber = -1;
                        string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");

                        // 2. Read Local Manifest ChangeNumber
                        try
                        {
                            var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                            if (manifestData != null && manifestData.TryGetValue("ChangeNumber", out string changeNumberStr) && long.TryParse(changeNumberStr, out localChangeNumber))
                            {
                                // Successfully read local change number
                            }
                            // If manifest not found or ChangeNumber missing/invalid, localChangeNumber remains -1
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi đọc manifest cục bộ cho profile {0} (AppID: {1})", profile.Name, profile.AppID);
                            // localChangeNumber remains -1 on error
                        }

                        // 3. Compare ChangeNumbers
                        bool needsUpdate = apiInfo.ChangeNumber > localChangeNumber;

                        // 4. Create ViewModel and Add to List
                        UpdateInfos.Add(new AppUpdateViewModel
                        {
                            ApiInfo = apiInfo,
                            NeedsUpdate = needsUpdate,
                            LocalChangeNumber = localChangeNumber // Store local number for display
                        });
                    }
                    else
                    {
                        // If API info could not be retrieved, still show the entry but indicate status
                        UpdateInfos.Add(new AppUpdateViewModel
                        {
                            ApiInfo = new AppUpdateInfo { AppID = profile.AppID, Name = profile.Name, LastUpdate = "Không thể lấy thông tin API" },
                            NeedsUpdate = false, // Cannot determine if update is needed without API info
                            LocalChangeNumber = -1 // Unknown local status
                        });
                        _logger.LogWarning("Không thể lấy thông tin API cho AppID {1} ({0}).", profile.Name, profile.AppID);
                    }
                }

                // Sort the list (e.g., by AppID or Name, or NeedsUpdate status)
                // Sorting by AppID or Name might be more stable for the UI
                UpdateInfos = UpdateInfos.OrderBy(vm => vm.ApiInfo.Name).ToList();
                // Or sort by update status (items needing update first)
                // UpdateInfos = UpdateInfos.OrderByDescending(vm => vm.NeedsUpdate).ThenBy(vm => vm.ApiInfo.Name).ToList();
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
                // Validation for UpdateCheckIntervalMinutes - Match SettingsPageModel
                if (UpdateCheckIntervalMinutes < 10 || UpdateCheckIntervalMinutes > 1440) // Example range: 10 mins to 24 hours
                {
                    StatusMessage = "Khoảng thời gian kiểm tra (phút) phải từ 10 đến 1440.";
                    IsSuccess = false;
                    // Reload data for the page
                    await OnGetAsync();
                    return Page(); // Return Page() to show validation error on the same page
                }

                // Use UpdateCheckService to update settings
                _updateCheckService.UpdateSettings(
                    UpdateCheckEnabled, // Use BindProperty value
                    TimeSpan.FromMinutes(UpdateCheckIntervalMinutes), // Use BindProperty value
                    AutoUpdateProfiles // Use BindProperty value for AutoUpdateProfiles
                );

                StatusMessage = $"Đã lưu cài đặt kiểm tra cập nhật: {(UpdateCheckEnabled ? "Bật" : "Tắt")}, {UpdateCheckIntervalMinutes} phút/lần, Tự động cập nhật: {(AutoUpdateProfiles ? "Bật" : "Tắt")}.";
                IsSuccess = true;

                // Reload data for the page after successful save
                await OnGetAsync();
                return Page(); // Return Page() instead of RedirectToPage() to keep TempData messages

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật");
                StatusMessage = $"Lỗi khi lưu cài đặt: {ex.Message}";
                IsSuccess = false;
                // Reload data for the page on error
                await OnGetAsync();
                return Page(); // Return Page() to show error message
            }
        }

        public async Task<IActionResult> OnPostCheckUpdatesAsync()
        {
            try
            {
                var profiles = await _profileService.GetAllProfiles();
                int checkedCount = 0;
                int gamesNeedingUpdateCount = 0; // Count games needing update, not just profiles queued

                // Replicate the core check logic here for a manual trigger.

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} trong kiểm tra thủ công do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
                    }

                    checkedCount++;
                    _logger.LogInformation("Kiểm tra cập nhật thủ công cho profile: {0} (AppID: {1})", profile.Name, profile.AppID);

                    long localChangeNumber = -1;
                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");

                    // 1. Đọc ChangeNumber từ manifest cục bộ
                    try
                    {
                        var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                        if (manifestData != null && manifestData.TryGetValue("ChangeNumber", out string changeNumberStr) && long.TryParse(changeNumberStr, out localChangeNumber))
                        {
                            _logger.LogInformation("Manifest cục bộ cho AppID {1} ({0}) có ChangeNumber: {2}", profile.Name, profile.AppID, localChangeNumber);
                        }
                        else
                        {
                            _logger.LogInformation("Không tìm thấy ChangeNumber trong manifest cục bộ cho AppID {1} ({0}) hoặc manifest không tồn tại. Coi như cần kiểm tra/cài đặt.", profile.Name, profile.AppID);
                            localChangeNumber = -1;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đọc manifest cục bộ cho profile {0} (AppID: {1}) trong kiểm tra thủ công", profile.Name, profile.AppID);
                        localChangeNumber = -1;
                    }

                    // 2. Lấy thông tin mới nhất từ Steam API (force refresh for manual check)
                    long latestApiChangeNumber = -1;
                    var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true); // Force refresh for manual check

                    if (latestAppInfo != null)
                    {
                        latestApiChangeNumber = latestAppInfo.ChangeNumber;
                        _logger.LogInformation("Steam API cho AppID {1} ({0}) có ChangeNumber mới nhất: {2}", profile.Name, profile.AppID, latestApiChangeNumber);
                    }
                    else
                    {
                        _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID {1} ({0}) trong kiểm tra thủ công.", profile.Name, profile.AppID);
                        continue; // Cannot check accurately without API info
                    }

                    // 3. So sánh ChangeNumber
                    bool needsUpdate = false;
                    if (latestApiChangeNumber > localChangeNumber)
                    {
                        _logger.LogInformation("Phát hiện cập nhật cho profile {0} (AppID: {1}): API ChangeNumber ({2}) > Local ChangeNumber ({3})",
                            profile.Name, profile.AppID, latestApiChangeNumber, localChangeNumber);
                        needsUpdate = true;
                        gamesNeedingUpdateCount++; // Increment count for games needing update
                    }
                    else
                    {
                        _logger.LogInformation("Không có cập nhật mới cho profile {0} (AppID: {1}): API ChangeNumber ({2}) <= Local ChangeNumber ({3})",
                           profile.Name, profile.AppID, latestApiChangeNumber, localChangeNumber);
                    }

                    // 4. If needs update, queue it for manual check
                    if (needsUpdate)
                    {
                        _logger.LogInformation("Kiểm tra thủ công phát hiện cập nhật. Đang thêm profile {0} (ID: {1}) vào hàng đợi...",
                            profile.Name, profile.Id);
                        await _steamCmdService.QueueProfileForUpdate(profile.Id);
                    }
                }

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã kiểm tra {checkedCount} game. Phát hiện {gamesNeedingUpdateCount} game có cập nhật mới. Các game cần cập nhật đã được thêm vào hàng đợi (nếu có)."
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
                // The cache is managed by SteamApiService.
                // We need a way to tell SteamApiService to clear its cache.
                // Call the ClearCacheAsync method on the injected SteamApiService
                await _steamApiService.ClearCacheAsync(); // This line should now work

                _logger.LogInformation("Đã yêu cầu xóa cache cập nhật từ SteamApiService");

                return new JsonResult(new { success = true, message = "Đã xóa cache cập nhật." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa cache cập nhật");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostUpdateAppAsync(string appId)
        {
            try
            {
                if (string.IsNullOrEmpty(appId))
                {
                    return new JsonResult(new { success = false, message = "Không có App ID được chỉ định." });
                }

                // Tìm tất cả profile có App ID này và đưa vào hàng đợi
                var profiles = await _profileService.GetAllProfiles();
                var matchingProfiles = profiles.Where(p => p.AppID == appId).ToList();

                if (!matchingProfiles.Any())
                {
                    return new JsonResult(new { success = false, message = $"Không tìm thấy profile nào có App ID: {appId}" });
                }

                // Đưa các profile có App ID tương ứng vào hàng đợi
                foreach (var profile in matchingProfiles)
                {
                    await _steamCmdService.QueueProfileForUpdate(profile.Id);
                }

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã thêm {matchingProfiles.Count} profile có App ID {appId} vào hàng đợi cập nhật."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật App ID {AppId}", appId);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
    }
}
