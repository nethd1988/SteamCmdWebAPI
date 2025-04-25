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
    public class UpdateManagementModel : PageModel
    {
        private readonly ILogger<UpdateManagementModel> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly IServiceProvider _serviceProvider;
        private readonly string _settingsFilePath;

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public List<AppUpdateInfo> UpdateInfos { get; set; } = new List<AppUpdateInfo>();

        [BindProperty]
        public UpdateCheckSettings Settings { get; set; } = new UpdateCheckSettings();

        public UpdateManagementModel(
            ILogger<UpdateManagementModel> logger,
            SteamApiService steamApiService,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _serviceProvider = serviceProvider;

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            _settingsFilePath = Path.Combine(dataDir, "update_check_settings.json");
            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                // Thử lấy từ service trước
                var updateCheckService = _serviceProvider.GetService<UpdateCheckService>();
                if (updateCheckService != null)
                {
                    Settings = updateCheckService.GetCurrentSettings();
                    return;
                }

                // Nếu không có service, đọc từ file
                if (System.IO.File.Exists(_settingsFilePath))
                {
                    string json = System.IO.File.ReadAllText(_settingsFilePath);
                    Settings = JsonConvert.DeserializeObject<UpdateCheckSettings>(json) ?? new UpdateCheckSettings();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải cài đặt kiểm tra cập nhật");
                Settings = new UpdateCheckSettings();
            }
        }

        private async Task SaveSettings()
        {
            try
            {
                // Log trước khi lưu để debug
                _logger.LogInformation("Lưu cài đặt: Enabled={0}, IntervalMinutes={1}, AutoUpdateProfiles={2}",
                    Settings.Enabled, Settings.IntervalMinutes, Settings.AutoUpdateProfiles);

                // Lưu file
                string json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
                await System.IO.File.WriteAllTextAsync(_settingsFilePath, json);

                // Cập nhật service nếu có thể
                var updateCheckService = _serviceProvider.GetService<UpdateCheckService>();
                if (updateCheckService != null)
                {
                    updateCheckService.UpdateSettings(
                        Settings.Enabled,
                        TimeSpan.FromMinutes(Settings.IntervalMinutes),
                        Settings.AutoUpdateProfiles);
                    _logger.LogInformation("Đã cập nhật service thành công");
                }
                else
                {
                    _logger.LogWarning("Không tìm thấy UpdateCheckService để cập nhật");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật");
                throw;
            }
        }

        public async Task OnGetAsync()
        {
            try
            {
                var profiles = await _profileService.GetAllProfiles();

                // Lấy thông tin cập nhật từ cache cho tất cả các profile
                foreach (var profile in profiles)
                {
                    if (!string.IsNullOrEmpty(profile.AppID))
                    {
                        var info = await _steamApiService.GetAppUpdateInfo(profile.AppID);
                        if (info != null)
                        {
                            UpdateInfos.Add(info);
                        }
                    }
                }

                // Sắp xếp theo thời gian cập nhật gần nhất
                UpdateInfos = UpdateInfos.OrderByDescending(i => i.LastUpdateDateTime).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải thông tin cập nhật");
                StatusMessage = $"Lỗi khi tải thông tin cập nhật: {ex.Message}";
                IsSuccess = false;
            }
        }

        public async Task<IActionResult> OnPostSaveSettingsAsync(bool enabled, int intervalMinutes, bool autoUpdateProfiles)
        {
            try
            {
                Settings.Enabled = enabled;
                Settings.IntervalMinutes = intervalMinutes;
                Settings.AutoUpdateProfiles = autoUpdateProfiles;

                await SaveSettings();

                StatusMessage = "Đã lưu cài đặt kiểm tra cập nhật thành công.";
                IsSuccess = true;

                return RedirectToPage();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật");
                StatusMessage = $"Lỗi khi lưu cài đặt: {ex.Message}";
                IsSuccess = false;
                return RedirectToPage();
            }
        }

        public async Task<IActionResult> OnPostCheckUpdatesAsync()
        {
            try
            {
                var profiles = await _profileService.GetAllProfiles();
                int checkedCount = 0;
                int updatedCount = 0;

                // Kiểm tra cập nhật cho tất cả profile
                foreach (var profile in profiles)
                {
                    if (!string.IsNullOrEmpty(profile.AppID))
                    {
                        checkedCount++;
                        bool hasUpdate = await _steamApiService.HasAppUpdate(profile.AppID);

                        if (hasUpdate && (profile.AutoRun || !Settings.AutoUpdateProfiles))
                        {
                            updatedCount++;
                            await _steamCmdService.QueueProfileForUpdate(profile.Id);
                        }
                    }
                }

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã kiểm tra {checkedCount} game, phát hiện {updatedCount} game có cập nhật mới."
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
                // Xóa tệp cache
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string cacheFilePath = Path.Combine(baseDirectory, "data", "steam_app_updates.json");

                if (System.IO.File.Exists(cacheFilePath))
                {
                    System.IO.File.Delete(cacheFilePath);
                    _logger.LogInformation("Đã xóa tệp cache cập nhật");
                }

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