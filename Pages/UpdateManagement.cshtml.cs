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
    // ViewModel để kết hợp thông tin API với trạng thái cập nhật cục bộ
    public class AppUpdateViewModel
    {
        public AppUpdateInfo ApiInfo { get; set; }
        public bool NeedsUpdate { get; set; }
        public bool HasChangedSinceLastCheck { get; set; }
        public long SizeOnDisk { get; set; }
        public string LastUpdateStatus { get; set; }
        public DateTime? LocalLastUpdated { get; set; }
        public string FormattedSize => SizeOnDisk > 0 ? $"{(SizeOnDisk / 1024.0 / 1024.0 / 1024.0):F2} GB" : "Không xác định";

        // Thông tin ChangeNumber từ các lần gọi API
        public long PreviousApiChangeNumber => ApiInfo?.LastCheckedChangeNumber ?? 0;
        public long CurrentApiChangeNumber => ApiInfo?.ChangeNumber ?? 0;
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
                // Lấy cài đặt từ service
                var currentSettings = _updateCheckService.GetCurrentSettings();
                UpdateCheckEnabled = currentSettings.Enabled;
                UpdateCheckIntervalMinutes = currentSettings.IntervalMinutes;
                AutoUpdateProfiles = currentSettings.AutoUpdateProfiles;

                // Giới hạn khoảng thời gian hợp lý
                if (UpdateCheckIntervalMinutes < 10) UpdateCheckIntervalMinutes = 10;
                if (UpdateCheckIntervalMinutes > 1440) UpdateCheckIntervalMinutes = 1440;

                var profiles = await _profileService.GetAllProfiles();

                // Xử lý từng profile để lấy thông tin API và trạng thái cục bộ
                foreach (var profile in profiles)
                {
                    if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    {
                        _logger.LogWarning("Bỏ qua profile {0} do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                        continue;
                    }

                    // 1. Lấy thông tin API
                    var apiInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID);
                    if (apiInfo == null)
                    {
                        // Nếu không lấy được thông tin API, vẫn hiển thị nhưng báo trạng thái
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
                        _logger.LogWarning("Không thể lấy thông tin API cho AppID {1} ({0}).", profile.Name, profile.AppID);
                        continue;
                    }

                    var viewModel = new AppUpdateViewModel
                    {
                        ApiInfo = apiInfo,
                        NeedsUpdate = false,
                        HasChangedSinceLastCheck = false,
                        SizeOnDisk = 0,
                        LastUpdateStatus = "Không xác định"
                    };

                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");

                    // 2. Đọc Manifest cục bộ
                    try
                    {
                        var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                        if (manifestData != null)
                        {
                            // Lấy SizeOnDisk
                            if (manifestData.TryGetValue("SizeOnDisk", out string sizeOnDiskStr) &&
                                long.TryParse(sizeOnDiskStr, out long sizeOnDisk))
                            {
                                viewModel.SizeOnDisk = sizeOnDisk;

                                // Cập nhật SizeOnDisk vào API Info để cache lại
                                await _steamApiService.UpdateSizeOnDiskFromManifest(profile.AppID, sizeOnDisk);
                            }

                            // Kiểm tra ChangeNumber từ các lần gọi API
                            if (apiInfo.LastCheckedChangeNumber > 0 &&
                                apiInfo.ChangeNumber != apiInfo.LastCheckedChangeNumber)
                            {
                                viewModel.HasChangedSinceLastCheck = true;
                                viewModel.LastUpdateStatus = "ChangeNumber thay đổi từ lần kiểm tra trước";
                                viewModel.NeedsUpdate = true;
                            }

                            // Kiểm tra LastUpdated
                            if (manifestData.TryGetValue("LastUpdated", out string lastUpdatedStr) &&
                                long.TryParse(lastUpdatedStr, out long lastUpdatedTimestamp))
                            {
                                var lastUpdatedDateTime = DateTimeOffset.FromUnixTimeSeconds(lastUpdatedTimestamp).DateTime;
                                viewModel.LocalLastUpdated = lastUpdatedDateTime;

                                if (apiInfo.LastUpdateDateTime.HasValue &&
                                    apiInfo.LastUpdateDateTime.Value > lastUpdatedDateTime)
                                {
                                    viewModel.LastUpdateStatus = "Thời gian cập nhật mới hơn";
                                    viewModel.NeedsUpdate = true;
                                }
                                else if (!viewModel.NeedsUpdate && !viewModel.HasChangedSinceLastCheck)
                                {
                                    viewModel.LastUpdateStatus = "Đã cập nhật";
                                }
                            }
                        }
                        else
                        {
                            viewModel.NeedsUpdate = true;
                            viewModel.LastUpdateStatus = "Không tìm thấy manifest";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đọc manifest cục bộ cho profile {0} (AppID: {1})", profile.Name, profile.AppID);
                        viewModel.LastUpdateStatus = "Lỗi đọc manifest";
                    }

                    // Thêm vào danh sách
                    UpdateInfos.Add(viewModel);
                }

                // Sắp xếp danh sách (theo NeedsUpdate và tên)
                UpdateInfos = UpdateInfos
                    .OrderByDescending(vm => vm.NeedsUpdate)
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
                // Kiểm tra tính hợp lệ của khoảng thời gian
                if (UpdateCheckIntervalMinutes < 10 || UpdateCheckIntervalMinutes > 1440)
                {
                    StatusMessage = "Khoảng thời gian kiểm tra (phút) phải từ 10 đến 1440.";
                    IsSuccess = false;
                    await OnGetAsync();
                    return Page();
                }

                // Cập nhật cài đặt qua UpdateCheckService
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

                    bool needsUpdate = false;
                    string updateReason = "";

                    // 1. Lấy thông tin API mới nhất (force refresh)
                    var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true);
                    if (latestAppInfo == null)
                    {
                        _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID {1} ({0}) trong kiểm tra thủ công.",
                            profile.Name, profile.AppID);
                        continue;
                    }

                    // 2. Kiểm tra ChangeNumber giữa các lần gọi API
                    if (latestAppInfo.LastCheckedChangeNumber > 0 &&
                        latestAppInfo.ChangeNumber != latestAppInfo.LastCheckedChangeNumber)
                    {
                        needsUpdate = true;
                        updateReason = $"ChangeNumber API thay đổi: {latestAppInfo.LastCheckedChangeNumber} -> {latestAppInfo.ChangeNumber}";
                    }

                    // 3. Kiểm tra LastUpdated từ manifest
                    if (!needsUpdate)
                    {
                        string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                        var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);

                        if (manifestData != null && latestAppInfo.LastUpdateDateTime.HasValue)
                        {
                            if (manifestData.TryGetValue("LastUpdated", out string lastUpdatedStr) &&
                                long.TryParse(lastUpdatedStr, out long lastUpdatedTimestamp))
                            {
                                var lastUpdatedDateTime = DateTimeOffset.FromUnixTimeSeconds(lastUpdatedTimestamp).DateTime;

                                if (latestAppInfo.LastUpdateDateTime.Value > lastUpdatedDateTime)
                                {
                                    needsUpdate = true;
                                    updateReason = $"Thời gian cập nhật API ({latestAppInfo.LastUpdateDateTime.Value}) > Local ({lastUpdatedDateTime})";
                                }
                            }
                            else
                            {
                                needsUpdate = true;
                                updateReason = "Không tìm thấy thông tin LastUpdated trong manifest";
                            }
                        }
                        else if (manifestData == null)
                        {
                            needsUpdate = true;
                            updateReason = "Không tìm thấy manifest cục bộ";
                        }
                    }

                    // 4. Xử lý kết quả
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

                // Tìm tất cả profile có App ID này và đưa vào hàng đợi
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