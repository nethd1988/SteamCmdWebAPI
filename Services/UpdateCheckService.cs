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
        private readonly QueueService _queueService;
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
            DependencyManagerService dependencyManagerService,
            QueueService queueService)
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
            _dependencyManagerService = dependencyManagerService;
            _queueService = queueService;

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

        public void UpdateSettings(bool enabled, TimeSpan interval, bool autoUpdateProfiles, bool useSteamKitNotifications)
        {
            lock (_settingsLock)
            {
                _enabled = enabled;
                _checkInterval = interval;
                _autoUpdateProfiles = autoUpdateProfiles;
                // Không sử dụng tham số này trong service vì chúng ta đã tắt logic cũ
                // Nhưng vẫn lưu vào cài đặt để giao diện có thể truy cập
            }

            _logger.LogInformation("Đã cập nhật cài đặt kiểm tra cập nhật: Enabled={0}, Interval={1} phút, AutoUpdateProfiles={2}, UseSteamKitNotifications={3}",
                _enabled, interval.TotalMinutes, _autoUpdateProfiles, useSteamKitNotifications);

            // Lưu cài đặt
            try
            {
                var settings = new UpdateCheckSettings
                {
                    Enabled = enabled,
                    IntervalMinutes = (int)interval.TotalMinutes,
                    AutoUpdateProfiles = autoUpdateProfiles,
                    UseSteamKitNotifications = useSteamKitNotifications
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
                    AutoUpdateProfiles = _autoUpdateProfiles,
                    UseSteamKitNotifications = true // Mặc định luôn bật
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

        private async Task CheckForUpdatesAsync()
        {
            _logger.LogInformation("Đang kiểm tra cập nhật cho tất cả các profile...");

            bool autoUpdateEnabled;
            lock (_settingsLock)
            {
                autoUpdateEnabled = _autoUpdateProfiles;
            }
            
            var profiles = await _profileService.GetAllProfiles();

            if (!profiles.Any())
            {
                _logger.LogInformation("Không có profile nào để kiểm tra cập nhật");
                return;
            }

            _logger.LogInformation("Tìm thấy {0} profile để kiểm tra", profiles.Count);
            bool anyUpdatesFound = false;
            var appsToUpdate = new List<(int profileId, string appId, string appName)>();

            // Kiểm tra từng profile
            foreach (var profile in profiles)
            {
                if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                    continue;

                // Kiểm tra app chính
                try {
                    var cachedInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: false);
                    var latestInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true);
                    
                    if (latestInfo != null && NeedsUpdate(cachedInfo, latestInfo))
                    {
                        anyUpdatesFound = true;
                        appsToUpdate.Add((profile.Id, profile.AppID, latestInfo.Name));
                    }
                    
                    // Kiểm tra apps phụ thuộc
                    var steamappsDir = Path.Combine(profile.InstallDirectory, "steamapps");
                    var dependentAppIds = await _dependencyManagerService.ScanDependenciesFromManifest(steamappsDir, profile.AppID);
                    
                    if (dependentAppIds.Any())
                    {
                        await _dependencyManagerService.UpdateDependenciesAsync(profile.Id, profile.AppID, dependentAppIds);
                        
                        foreach (var appId in dependentAppIds)
                        {
                            cachedInfo = await _steamApiService.GetAppUpdateInfo(appId, forceRefresh: false);
                            latestInfo = await _steamApiService.GetAppUpdateInfo(appId, forceRefresh: true);
                            
                            if (latestInfo != null && NeedsUpdate(cachedInfo, latestInfo))
                            {
                                anyUpdatesFound = true;
                                await _dependencyManagerService.MarkAppForUpdateAsync(appId);
                                appsToUpdate.Add((profile.Id, appId, latestInfo.Name));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra profile '{0}'", profile.Name);
                }
            }

            // Thêm vào hàng đợi cập nhật nếu cần
            if (anyUpdatesFound && autoUpdateEnabled && appsToUpdate.Count > 0)
            {
                _logger.LogInformation("Phát hiện {0} apps cần cập nhật. Thêm vào hàng đợi...", appsToUpdate.Count);
                
                // Xử lý tất cả cùng lúc để giảm độ trễ
                foreach (var (profileId, appId, appName) in appsToUpdate)
                {
                    try
                    {
                        // Kiểm tra xem đã có trong hàng đợi chưa
                        if (_queueService.IsAlreadyInQueue(profileId, appId))
                        {
                            _logger.LogInformation("CheckForUpdatesAsync: AppID {AppId} đã có trong hàng đợi, bỏ qua", appId);
                            continue;
                        }
                        
                        await _steamCmdService.RunSpecificAppAsync(profileId, appId);
                        _logger.LogInformation("Đã thêm app '{0}' (ID: {1}) vào hàng đợi cập nhật", appName, appId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi thêm app '{0}' (ID: {1}) vào hàng đợi", appName, appId);
                    }
                }
            }
            else if (anyUpdatesFound)
            {
                _logger.LogInformation("Phát hiện cập nhật nhưng tự động cập nhật đang tắt");
            }

            await _steamApiService.SaveCachedAppInfo();
        }

        private bool NeedsUpdate(AppUpdateInfo cached, AppUpdateInfo latest)
        {
            if (cached == null) return true;
            
            // Kiểm tra change number
            if (cached.LastCheckedChangeNumber > 0 && latest.ChangeNumber != cached.LastCheckedChangeNumber)
                return true;
                
            // Kiểm tra thời gian cập nhật
            if (cached.LastCheckedUpdateDateTime.HasValue && latest.LastUpdateDateTime.HasValue &&
                latest.LastUpdateDateTime.Value != cached.LastCheckedUpdateDateTime.Value)
                return true;
                
            return false;
        }
    }

  
}