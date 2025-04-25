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
    // ViewModel để kết hợp thông tin API với trạng thái cập nhật
    public class AppUpdateViewModel
    {
        public AppUpdateInfo ApiInfo { get; set; }
        public bool NeedsUpdate { get; set; }
        public bool HasChangedSinceLastCheck { get; set; }
        public long SizeOnDisk { get; set; }
        public string LastUpdateStatus { get; set; }
        public string FormattedSize => SizeOnDisk > 0 ? $"{(SizeOnDisk / 1024.0 / 1024.0 / 1024.0):F2} GB" : "Không xác định";

        // Thông tin ChangeNumber từ các lần gọi API
        public long PreviousApiChangeNumber => ApiInfo?.LastCheckedChangeNumber ?? 0;
        public long CurrentApiChangeNumber => ApiInfo?.ChangeNumber ?? 0;

        // Thông tin thời gian cập nhật từ các lần gọi API
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
                // Lấy cài đặt từ service
                var currentSettings = _updateCheckService.GetCurrentSettings();
                UpdateCheckEnabled = currentSettings.Enabled;
                UpdateCheckIntervalMinutes = currentSettings.IntervalMinutes;
                AutoUpdateProfiles = currentSettings.AutoUpdateProfiles;

                // Giới hạn khoảng thời gian hợp lý
                if (UpdateCheckIntervalMinutes < 10) UpdateCheckIntervalMinutes = 10;
                if (UpdateCheckIntervalMinutes > 1440) UpdateCheckIntervalMinutes = 1440;

                var profiles = await _profileService.GetAllProfiles();

                // Xử lý từng profile để lấy thông tin API và trạng thái cập nhật
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
                        SizeOnDisk = apiInfo.SizeOnDisk,
                        LastUpdateStatus = "Đã cập nhật"
                    };

                    // 2. Đọc Manifest cục bộ để lấy thông tin SizeOnDisk (nếu chưa có)
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

                                // Cập nhật SizeOnDisk vào API Info để cache lại
                                await _steamApiService.UpdateSizeOnDiskFromManifest(profile.AppID, sizeOnDisk);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đọc manifest cục bộ cho profile {0} (AppID: {1})", profile.Name, profile.AppID);
                    }

                    // 3. Kiểm tra ChangeNumber giữa các lần gọi API
                    if (apiInfo.LastCheckedChangeNumber > 0 && apiInfo.ChangeNumber != apiInfo.LastCheckedChangeNumber)
                    {
                        viewModel.HasChangedSinceLastCheck = true;
                        viewModel.LastUpdateStatus = "ChangeNumber thay đổi từ lần kiểm tra trước";
                        viewModel.NeedsUpdate = true;
                    }

                    // 4. Kiểm tra thời gian cập nhật giữa các lần gọi API
                    else if (apiInfo.LastCheckedUpdateDateTime.HasValue &&
                            apiInfo.LastUpdateDateTime.HasValue &&
                            apiInfo.LastUpdateDateTime.Value != apiInfo.LastCheckedUpdateDateTime.Value)
                    {
                        viewModel.HasChangedSinceLastCheck = true;
                        viewModel.LastUpdateStatus = "Thời gian cập nhật thay đổi từ lần kiểm tra trước";
                        viewModel.NeedsUpdate = true;
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

                    // Lấy thông tin API đã cached trước
                    var cachedAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: false);
                    long previousChangeNumber = cachedAppInfo?.LastCheckedChangeNumber ?? 0;
                    DateTime? previousUpdateDateTime = cachedAppInfo?.LastCheckedUpdateDateTime;

                    // Lấy thông tin API mới nhất (force refresh)
                    var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true);
                    if (latestAppInfo == null)
                    {
                        _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID {1} ({0}) trong kiểm tra thủ công.",
                            profile.Name, profile.AppID);
                        continue;
                    }

                    // Kiểm tra cập nhật dựa trên ChangeNumber và thời gian cập nhật
                    bool needsUpdate = false;
                    string updateReason = "";

                    // Kiểm tra ChangeNumber giữa các lần gọi API
                    if (previousChangeNumber > 0 && latestAppInfo.ChangeNumber != previousChangeNumber)
                    {
                        needsUpdate = true;
                        updateReason = $"ChangeNumber API thay đổi: {previousChangeNumber} -> {latestAppInfo.ChangeNumber}";
                        _logger.LogInformation("Phát hiện thay đổi ChangeNumber cho {0} (AppID: {1}): {2} -> {3}",
                            profile.Name, profile.AppID, previousChangeNumber, latestAppInfo.ChangeNumber);
                    }
                    // Kiểm tra thời gian cập nhật giữa các lần gọi API
                    else if (previousUpdateDateTime.HasValue &&
                            latestAppInfo.LastUpdateDateTime.HasValue &&
                            latestAppInfo.LastUpdateDateTime.Value != previousUpdateDateTime.Value)
                    {
                        needsUpdate = true;
                        updateReason = $"Thời gian cập nhật thay đổi: {previousUpdateDateTime.Value:dd/MM/yyyy HH:mm:ss} -> {latestAppInfo.LastUpdateDateTime.Value:dd/MM/yyyy HH:mm:ss}";
                        _logger.LogInformation("Phát hiện thay đổi thời gian cập nhật cho {0} (AppID: {1}): {2:dd/MM/yyyy HH:mm:ss} -> {3:dd/MM/yyyy HH:mm:ss}",
                            profile.Name, profile.AppID,
                            previousUpdateDateTime.Value,
                            latestAppInfo.LastUpdateDateTime.Value);
                    }
                    else if (previousChangeNumber == 0 || !previousUpdateDateTime.HasValue)
                    {
                        // Lần đầu tiên kiểm tra, ghi nhận ChangeNumber hiện tại
                        _logger.LogInformation("Lần đầu tiên kiểm tra {0} (AppID: {1}). Ghi nhận ChangeNumber: {2}, Thời gian cập nhật: {3}",
                            profile.Name, profile.AppID, latestAppInfo.ChangeNumber,
                            latestAppInfo.LastUpdateDateTime.HasValue ? latestAppInfo.LastUpdateDateTime.Value.ToString("dd/MM/yyyy HH:mm:ss") : "không có");
                    }

                    // Cập nhật SizeOnDisk từ manifest (nếu có)
                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                    var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                    if (manifestData != null && manifestData.TryGetValue("SizeOnDisk", out string sizeOnDiskStr) &&
                        long.TryParse(sizeOnDiskStr, out long sizeOnDisk))
                    {
                        await _steamApiService.UpdateSizeOnDiskFromManifest(profile.AppID, sizeOnDisk);
                        _logger.LogInformation("Đã cập nhật SizeOnDisk = {0} bytes từ manifest cho AppID {1}",
                            sizeOnDisk, profile.AppID);
                    }

                    // Xử lý kết quả
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

                // Đảm bảo lưu cache sau khi cập nhật
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