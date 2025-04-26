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
    }

    public class UpdateManagementModel : PageModel
    {
        private readonly ILogger<UpdateManagementModel> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly UpdateCheckService _updateCheckService;

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
            UpdateCheckService updateCheckService)
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _updateCheckService = updateCheckService;
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

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
                    }

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
                            LastUpdateStatus = "Không xác định"
                        });
                        continue;
                    }

                    var viewModel = new AppUpdateViewModel
                    {
                        ApiInfo = apiInfo,
                        NeedsUpdate = false,
                        HasChangedSinceLastCheck = false,
                        SizeOnDisk = apiInfo.SizeOnDisk,
                        LastUpdateStatus = "Đã cập nhật"
                    };

                    try
                    {
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

                    UpdateInfos.Add(viewModel);
                }

                UpdateInfos = UpdateInfos
                    .OrderByDescending(vm => vm.CurrentUpdateDateTime)
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
                int gamesNeedingUpdateCount = 0;

                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} trong kiểm tra thủ công do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
                    }

                    checkedCount++;
                    _logger.LogInformation("Kiểm tra cập nhật thủ công cho profile: {0} (AppID: {1})", profile.Name, profile.AppID);

                    var cachedAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: false);
                    long previousChangeNumber = cachedAppInfo?.LastCheckedChangeNumber ?? 0;
                    DateTime? previousUpdateDateTime = cachedAppInfo?.LastCheckedUpdateDateTime;

                    var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true);
                    if (latestAppInfo == null)
                    {
                        _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID {1} ({0}) trong kiểm tra thủ công.",
                            profile.Name, profile.AppID);
                        continue;
                    }

                    bool needsUpdate = false;
                    string updateReason = "";

                    if (previousChangeNumber > 0 && latestAppInfo.ChangeNumber != previousChangeNumber)
                    {
                        needsUpdate = true;
                        updateReason = $"ChangeNumber API thay đổi: {previousChangeNumber} -> {latestAppInfo.ChangeNumber}";
                        _logger.LogInformation("Phát hiện thay đổi ChangeNumber cho {0} (AppID: {1}): {2} -> {3}",
                            profile.Name, profile.AppID, previousChangeNumber, latestAppInfo.ChangeNumber);
                    }
                    else if (previousChangeNumber == 0 || !previousUpdateDateTime.HasValue)
                    {
                        _logger.LogInformation("Lần đầu tiên kiểm tra {0} (AppID: {1}). Ghi nhận ChangeNumber: {2}, Thời gian cập nhật: {3}",
                            profile.Name, profile.AppID, latestAppInfo.ChangeNumber,
                            latestAppInfo.LastUpdateDateTime.HasValue ? latestAppInfo.LastUpdateDateTime.Value.ToString("dd/MM/yyyy HH:mm:ss") : "không có");
                    }

                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                    var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                    if (manifestData != null && manifestData.TryGetValue("SizeOnDisk", out string sizeOnDiskStr) &&
                        long.TryParse(sizeOnDiskStr, out long sizeOnDisk))
                    {
                        await _steamApiService.UpdateSizeOnDiskFromManifest(profile.AppID, sizeOnDisk);
                    }

                    if (needsUpdate)
                    {
                        gamesNeedingUpdateCount++;
                        _logger.LogInformation("Phát hiện cập nhật cho {0} (AppID: {1}): {2}",
                            profile.Name, profile.AppID, updateReason);

                        await _steamCmdService.QueueProfileForUpdate(profile.Id);
                    }
                    else
                    {
                        _logger.LogInformation("Không phát hiện cập nhật cho {0} (AppID: {1})",
                            profile.Name, profile.AppID);
                    }
                }

                await _steamApiService.SaveCachedAppInfo();

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã kiểm tra {checkedCount} game. Phát hiện {gamesNeedingUpdateCount} game có cập nhật mới. " +
                              "Các game cần cập nhật đã được thêm vào hàng đợi."
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

        public async Task<IActionResult> OnPostUpdateAppAsync(string appId)
        {
            try
            {
                if (string.IsNullOrEmpty(appId))
                {
                    return new JsonResult(new { success = false, message = "Không có App ID được chỉ định." });
                }

                var profiles = await _profileService.GetAllProfiles();
                var matchingProfiles = profiles.Where(p => p.AppID == appId).ToList();

                if (!matchingProfiles.Any())
                {
                    return new JsonResult(new
                    {
                        success = false,
                        message = $"Không tìm thấy profile nào có App ID: {appId}"
                    });
                }

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