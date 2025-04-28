using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using SteamCmdWebAPI.Models;
using System.Collections.Generic;

namespace SteamCmdWebAPI.Services
{
    public class UpdateCheckService : BackgroundService
    {
        private readonly ILogger<UpdateCheckService> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private readonly DependencyManagerService _dependencyManagerService;
        private TimeSpan _checkInterval = TimeSpan.FromMinutes(10);
        private bool _enabled = true;
        private bool _autoUpdateProfiles = true;
        private readonly string _settingsFilePath;
        private readonly object _settingsLock = new object();

        public UpdateCheckService(
            ILogger<UpdateCheckService> logger,
            SteamApiService steamApiService,
            ProfileService profileService,
            SteamCmdService steamCmdService,
            DependencyManagerService dependencyManagerService)
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _dependencyManagerService = dependencyManagerService;

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            _settingsFilePath = Path.Combine(dataDir, "update_check_settings.json");

            // Các giá trị mặc định
            _checkInterval = TimeSpan.FromMinutes(10);
            _enabled = true;
            _autoUpdateProfiles = true;

            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<UpdateCheckSettings>(json);

                    if (settings != null)
                    {
                        lock (_settingsLock)
                        {
                            _enabled = settings.Enabled;
                            _checkInterval = TimeSpan.FromMinutes(settings.IntervalMinutes);
                            _autoUpdateProfiles = settings.AutoUpdateProfiles;
                        }

                        _logger.LogInformation("Đã tải cài đặt kiểm tra cập nhật: Enabled={0}, IntervalMinutes={1}, AutoUpdateProfiles={2}",
                            _enabled, settings.IntervalMinutes, _autoUpdateProfiles);
                    }
                    else
                    {
                        _logger.LogWarning("File settings kiểm tra cập nhật '{SettingsFilePath}' rỗng hoặc không hợp lệ. Sử dụng cài đặt mặc định.", _settingsFilePath);
                    }
                }
                else
                {
                    _logger.LogInformation("File settings kiểm tra cập nhật '{SettingsFilePath}' không tồn tại. Sử dụng cài đặt mặc định: Enabled={0}, IntervalMinutes={1}, AutoUpdateProfiles={2}",
                         _settingsFilePath, _enabled, _checkInterval.TotalMinutes, _autoUpdateProfiles);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải cài đặt kiểm tra cập nhật từ '{SettingsFilePath}'", _settingsFilePath);
            }
        }

        public void UpdateSettings(bool enabled, TimeSpan interval, bool autoUpdateProfiles)
        {
            lock (_settingsLock)
            {
                _enabled = enabled;
                _checkInterval = interval;
                _autoUpdateProfiles = autoUpdateProfiles;
            }

            _logger.LogInformation("Đã cập nhật cài đặt kiểm tra cập nhật: Enabled={0}, Interval={1} phút, AutoUpdateProfiles={2}",
                _enabled, interval.TotalMinutes, _autoUpdateProfiles);

            // Lưu cài đặt
            try
            {
                var settings = new UpdateCheckSettings
                {
                    Enabled = enabled,
                    IntervalMinutes = (int)interval.TotalMinutes,
                    AutoUpdateProfiles = autoUpdateProfiles
                };

                string json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(_settingsFilePath, json);
                _logger.LogInformation("Đã lưu cài đặt kiểm tra cập nhật vào '{SettingsFilePath}'", _settingsFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật vào '{SettingsFilePath}'", _settingsFilePath);
            }
        }

        public UpdateCheckSettings GetCurrentSettings()
        {
            lock (_settingsLock)
            {
                return new UpdateCheckSettings
                {
                    Enabled = _enabled,
                    IntervalMinutes = (int)_checkInterval.TotalMinutes,
                    AutoUpdateProfiles = _autoUpdateProfiles
                };
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Dịch vụ kiểm tra cập nhật đã khởi động. Kiểm tra mỗi {0} phút.", _checkInterval.TotalMinutes);

            // Đợi khởi động cho tất cả dịch vụ khác
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                bool isEnabled;
                TimeSpan interval;

                lock (_settingsLock)
                {
                    isEnabled = _enabled;
                    interval = _checkInterval;
                }

                if (isEnabled)
                {
                    try
                    {
                        await CheckForUpdatesAsync();
                    }
                    catch (Exception ex)
                    {
                        // This catch block is for exceptions outside the per-profile loop
                        _logger.LogError(ex, "Lỗi nghiêm trọng trong quá trình kiểm tra cập nhật tự động");
                    }
                }
                else
                {
                    _logger.LogDebug("Kiểm tra cập nhật tự động đang bị tắt");
                }

                // Đợi đến lần kiểm tra tiếp theo
                await Task.Delay(interval, stoppingToken);
            }
        }

        // Trong Services/UpdateCheckService.cs, điều chỉnh phương thức CheckForUpdatesAsync() để đảm bảo nhất quán khi cập nhật lịch trình

        private async Task CheckForUpdatesAsync()
        {
            _logger.LogInformation("Đang kiểm tra cập nhật cho tất cả các profile...");

            bool autoUpdateEnabled;
            lock (_settingsLock)
            {
                autoUpdateEnabled = _autoUpdateProfiles;
            }
            _logger.LogDebug("Cài đặt AutoUpdateProfiles hiện tại: {AutoUpdateProfiles}", autoUpdateEnabled);

            var profiles = await _profileService.GetAllProfiles();

            if (!profiles.Any())
            {
                _logger.LogInformation("Không có profile nào để kiểm tra cập nhật");
                return;
            }

            _logger.LogInformation("Tìm thấy {0} profile để kiểm tra", profiles.Count);
            bool anyUpdatesFound = false;

            // Kiểm tra từng app riêng biệt (chính và phụ thuộc)
            foreach (var profile in profiles)
            {
                _logger.LogInformation("--- Đang kiểm tra profile '{ProfileName}' (ID: {ProfileId}) ---",
                                    profile.Name, profile.Id);

                if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                {
                    _logger.LogWarning("Bỏ qua profile '{0}' (ID: {1}) do thiếu AppID hoặc Thư mục cài đặt.", profile.Name, profile.Id);
                    continue;
                }

                // Kiểm tra app chính
                try
                {
                    _logger.LogDebug("Đang kiểm tra app chính: {AppId}", profile.AppID);
                    var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true);

                    if (latestAppInfo == null)
                    {
                        _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID chính {1} ('{0}'). Bỏ qua.", profile.Name, profile.AppID);
                        continue;
                    }

                    bool needsUpdate = false;

                    if (latestAppInfo.LastCheckedChangeNumber > 0 && latestAppInfo.ChangeNumber != latestAppInfo.LastCheckedChangeNumber)
                    {
                        needsUpdate = true;
                        string updateReason = $"ChangeNumber API thay đổi: {latestAppInfo.LastCheckedChangeNumber} -> {latestAppInfo.ChangeNumber}";
                        _logger.LogInformation("==> Phát hiện thay đổi ChangeNumber cho app chính '{0}' (AppID: {1}): {2}",
                                            profile.Name, profile.AppID, updateReason);
                    }

                    // Cập nhật SizeOnDisk từ manifest nếu có thể
                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                    try
                    {
                        var manifestData = await _steamCmdService.ReadAppManifest(steamappsDir, profile.AppID);
                        if (manifestData != null && manifestData.TryGetValue("SizeOnDisk", out string sizeOnDiskStr) &&
                            long.TryParse(sizeOnDiskStr, out long sizeOnDisk))
                        {
                            await _steamApiService.UpdateSizeOnDiskFromManifest(profile.AppID, sizeOnDisk);
                        }
                    }
                    catch (Exception manifestEx)
                    {
                        _logger.LogWarning(manifestEx, "Lỗi khi đọc manifest cho app chính '{ProfileName}' (AppID: {AppId})", profile.Name, profile.AppID);
                    }

                    if (needsUpdate)
                    {
                        anyUpdatesFound = true;

                        if (autoUpdateEnabled)
                        {
                            // Nếu tự động cập nhật được bật, chúng ta sẽ đặt _isRunningAllProfiles = true
                            // để đảm bảo cập nhật tất cả app (chính và phụ thuộc) khi chạy từ lịch trình
                            _logger.LogInformation("--> AutoUpdateProfiles được bật. Thêm profile '{0}' vào hàng đợi để cập nhật tất cả app...",
                                profile.Name);

                            // Thêm vào hàng đợi để cập nhật tất cả (chính và phụ thuộc)
                            // Tạm thời đặt _isRunningAllProfiles = true sẽ được xử lý bên trong RunAllProfilesAsync
                            bool success = await _steamCmdService.QueueProfileForUpdate(profile.Id);

                            if (success)
                            {
                                _logger.LogInformation("--> Đã thêm profile '{0}' vào hàng đợi thành công.", profile.Name);
                            }
                            else
                            {
                                _logger.LogError("--> LỖI: Không thể thêm profile '{0}' vào hàng đợi.", profile.Name);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("--> AutoUpdateProfiles đang tắt. KHÔNG tự động cập nhật profile '{0}'.", profile.Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra app chính của profile '{0}' (ID: {1})", profile.Name, profile.Id);
                }

                // Kiểm tra các app phụ thuộc
                try
                {
                    string steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                    var dependentAppIds = await _dependencyManagerService.ScanDependenciesFromManifest(steamappsDir, profile.AppID);

                    if (dependentAppIds.Any())
                    {
                        _logger.LogInformation("Đang kiểm tra {0} app phụ thuộc của profile '{1}'", dependentAppIds.Count, profile.Name);

                        // Cập nhật danh sách phụ thuộc vào cơ sở dữ liệu
                        await _dependencyManagerService.UpdateDependenciesAsync(profile.Id, profile.AppID, dependentAppIds);

                        foreach (var appId in dependentAppIds)
                        {
                            _logger.LogDebug("Đang kiểm tra app phụ thuộc: {AppId}", appId);
                            var appInfo = await _steamApiService.GetAppUpdateInfo(appId, forceRefresh: true);

                            if (appInfo == null)
                            {
                                _logger.LogWarning("Không thể lấy thông tin Steam API cho app phụ thuộc {0}", appId);
                                continue;
                            }

                            bool needsUpdate = false;

                            if (appInfo.LastCheckedChangeNumber > 0 && appInfo.ChangeNumber != appInfo.LastCheckedChangeNumber)
                            {
                                needsUpdate = true;
                                string updateReason = $"ChangeNumber API thay đổi: {appInfo.LastCheckedChangeNumber} -> {appInfo.ChangeNumber}";
                                _logger.LogInformation("==> Phát hiện thay đổi ChangeNumber cho app phụ thuộc (AppID: {0}): {1}", appId, updateReason);
                            }

                            if (needsUpdate)
                            {
                                anyUpdatesFound = true;

                                // Đánh dấu app cần cập nhật
                                await _dependencyManagerService.MarkAppForUpdateAsync(appId);

                                // Lưu ý: Không cần tự động cập nhật riêng từng app phụ thuộc ở đây
                                // vì nếu autoUpdateEnabled = true thì đã thêm toàn bộ profile 
                                // vào hàng đợi ở trên (sẽ tự động cập nhật tất cả app phụ thuộc)
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra các app phụ thuộc của profile '{0}' (ID: {1})", profile.Name, profile.Id);
                }

                _logger.LogInformation("--- Kết thúc kiểm tra profile '{ProfileName}' ---", profile.Name);
            }

            if (!anyUpdatesFound)
            {
                _logger.LogInformation("Không phát hiện cập nhật mới cần xử lý cho bất kỳ app nào đã kiểm tra.");
            }
            else
            {
                _logger.LogInformation("Đã hoàn thành kiểm tra cập nhật. Ít nhất một app đã được đánh dấu cần cập nhật.");
            }
        }
    }

    // Sử dụng trực tiếp class từ Models thay vì định nghĩa lại
    // Class đã được định nghĩa trong Models/UpdateCheckSettings.cs
}