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

        private async Task CheckForUpdatesAsync()
        {
            _logger.LogInformation("Đang kiểm tra cập nhật cho tất cả các profile và app phụ thuộc...");

            bool autoUpdateEnabled;
            lock (_settingsLock)
            {
                autoUpdateEnabled = _autoUpdateProfiles;
            }
            _logger.LogDebug("Cài đặt AutoUpdateProfiles hiện tại: {AutoUpdateProfiles}", autoUpdateEnabled);

            var profiles = await _profileService.GetAllProfiles();
            var allDependencies = await _dependencyManagerService.GetAllDependenciesAsync();

            // Tạo danh sách các app cần kiểm tra (cả chính và phụ thuộc)
            var profilesToCheck = new List<SteamCmdProfile>();

            foreach (var profile in profiles)
            {
                // Thêm profile vào danh sách kiểm tra chính
                profilesToCheck.Add(profile);

                // Kiểm tra các app phụ thuộc
                var dependency = allDependencies.FirstOrDefault(d => d.ProfileId == profile.Id);
                if (dependency != null && dependency.DependentApps.Any())
                {
                    foreach (var app in dependency.DependentApps)
                    {
                        // Tạo profile ảo cho app phụ thuộc để kiểm tra cập nhật
                        var virtualProfile = new SteamCmdProfile
                        {
                            Id = profile.Id, // Giữ ID của profile gốc để liên kết
                            Name = $"{profile.Name} - {app.Name}", // Tên gợi nhớ
                            AppID = app.AppId, // AppID của app phụ thuộc
                            InstallDirectory = profile.InstallDirectory // Thư mục cài đặt của profile gốc
                        };

                        profilesToCheck.Add(virtualProfile);
                    }
                }
            }

            if (!profilesToCheck.Any())
            {
                _logger.LogInformation("Không có profile hoặc app nào để kiểm tra cập nhật");
                return;
            }

            _logger.LogInformation("Tìm thấy {0} profile/app để kiểm tra", profilesToCheck.Count);
            bool anyUpdatesFound = false;

            // Vòng lặp chính giờ sẽ duyệt qua profilesToCheck (bao gồm cả profile gốc và ảo)
            foreach (var profile in profilesToCheck)
            {
                _logger.LogInformation("--- Đang xử lý profile/app '{ProfileName}' (ID Profile gốc: {ProfileId}, AppID: {AppId}) ---",
                                     profile.Name, profile.Id, profile.AppID);

                if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                {
                    _logger.LogWarning("Bỏ qua '{0}' (ID Profile gốc: {1}) do thiếu AppID hoặc Thư mục cài đặt.", profile.Name, profile.Id);
                    continue;
                }

                try
                {
                    _logger.LogDebug("Đang lấy thông tin Steam API cho AppID: {AppId}", profile.AppID);
                    // Lấy thông tin từ Steam API với force refresh
                    var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID, forceRefresh: true);

                    if (latestAppInfo == null)
                    {
                        _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID {1} ('{0}'). Không thể kiểm tra cập nhật.", profile.Name, profile.AppID);
                        continue;
                    }

                    _logger.LogDebug("Thông tin API cho '{ProfileName}' (AppID: {AppId}): ChangeNumber={ChangeNumber}, LastCheckedChangeNumber={LastCheckedChangeNumber}",
                                   profile.Name, profile.AppID, latestAppInfo.ChangeNumber, latestAppInfo.LastCheckedChangeNumber);


                    // Kiểm tra cập nhật dựa vào ChangeNumber
                    bool needsUpdate = false;
                    string updateReason = "";

                    // Kiểm tra ChangeNumber giữa các lần gọi API
                    if (latestAppInfo.LastCheckedChangeNumber > 0 && latestAppInfo.ChangeNumber != latestAppInfo.LastCheckedChangeNumber)
                    {
                        needsUpdate = true;
                        updateReason = $"ChangeNumber API thay đổi: {latestAppInfo.LastCheckedChangeNumber} -> {latestAppInfo.ChangeNumber}";
                        _logger.LogInformation("==> Phát hiện thay đổi ChangeNumber cho '{0}' (AppID: {1}): {2}",
                                               profile.Name, profile.AppID, updateReason);
                    }
                    else if (latestAppInfo.LastCheckedChangeNumber == 0)
                    {
                        _logger.LogInformation("Đây là lần đầu tiên kiểm tra '{0}' (AppID: {1}). Ghi nhận ChangeNumber hiện tại: {2}",
                                               profile.Name, profile.AppID, latestAppInfo.ChangeNumber);
                    }
                    else
                    {
                        _logger.LogInformation("Không có thay đổi ChangeNumber cho '{0}' (AppID: {1}): vẫn là {2}",
                                               profile.Name, profile.AppID, latestAppInfo.ChangeNumber);
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
                        else
                        {
                            _logger.LogDebug("Không thể đọc SizeOnDisk từ manifest cho '{ProfileName}' (AppID: {AppId})", profile.Name, profile.AppID);
                        }
                    }
                    catch (Exception manifestEx)
                    {
                        _logger.LogWarning(manifestEx, "Lỗi khi đọc manifest cho '{ProfileName}' (AppID: {AppId}). Bỏ qua cập nhật SizeOnDisk.", profile.Name, profile.AppID);
                    }

                    if (needsUpdate)
                    {
                        anyUpdatesFound = true;

                        // Xác định xem đây là app chính hay app phụ thuộc
                        var dependency = await _dependencyManagerService.GetDependencyByProfileIdAsync(profile.Id);
                        bool isMainApp = dependency == null || dependency.MainAppId == profile.AppID;

                        if (isMainApp)
                        {
                            // Nếu là app chính, xử lý như profile bình thường
                            if (autoUpdateEnabled)
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles được bật. Đang thêm profile '{0}' (ID: {1}, AppID chính: {2}) VÀO HÀNG ĐỢI cập nhật...",
                                    profile.Name, profile.Id, profile.AppID);

                                // Sử dụng ID của profile gốc để thêm vào hàng đợi
                                bool queueSuccess = await _steamCmdService.QueueProfileForUpdate(profile.Id);

                                if (queueSuccess)
                                {
                                    _logger.LogInformation("--> Đã thêm profile '{0}' (ID: {1}) vào hàng đợi thành công.", profile.Name, profile.Id);
                                }
                                else
                                {
                                    _logger.LogError("--> LỖI: Không thể thêm profile '{0}' (ID: {1}) vào hàng đợi.", profile.Name, profile.Id);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles đang tắt. KHÔNG tự động thêm profile '{0}' (ID: {1}) vào hàng đợi dù phát hiện cập nhật.", profile.Name, profile.Id);
                            }
                        }
                        else // Nếu là app phụ thuộc
                        {
                            // Nếu là app phụ thuộc, đánh dấu app cần cập nhật
                            await _dependencyManagerService.MarkAppForUpdateAsync(profile.AppID);

                            if (autoUpdateEnabled)
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles được bật. Đang thiết lập App phụ thuộc ID: {0} để cập nhật trong profile '{1}' (ID gốc: {2})...",
                                    profile.AppID, profile.Name, profile.Id);

                                // Gọi hàm cập nhật app cụ thể, sử dụng ID profile gốc và AppID phụ thuộc
                                bool queueSuccess = await _steamCmdService.RunSpecificAppAsync(profile.Id, profile.AppID);

                                if (queueSuccess)
                                {
                                    _logger.LogInformation("--> Đã thiết lập cập nhật App ID {0} thành công.", profile.AppID);
                                }
                                else
                                {
                                    _logger.LogError("--> LỖI: Không thể cập nhật App ID {0} của profile '{1}' (ID gốc: {2}).", profile.AppID, profile.Name, profile.Id);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles đang tắt. KHÔNG tự động cập nhật App ID {0} của profile '{1}' (ID gốc: {2}) dù phát hiện cập nhật.",
                                    profile.AppID, profile.Name, profile.Id);
                            }
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Không có cập nhật mới cần xử lý cho '{0}' (AppID: {1}).", profile.Name, profile.AppID);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi xử lý '{0}' (ID Profile gốc: {1}, AppID: {2}) trong quá trình kiểm tra cập nhật.",
                                     profile.Name, profile.Id, profile.AppID);
                }

                _logger.LogInformation("--- Kết thúc xử lý '{ProfileName}' ---", profile.Name);

            }

            if (!anyUpdatesFound)
            {
                _logger.LogInformation("Không phát hiện cập nhật mới cần xử lý cho bất kỳ profile/app nào đã kiểm tra.");
            }
            else
            {
                _logger.LogInformation("Đã hoàn thành kiểm tra cập nhật. Ít nhất một profile/app đã được đánh dấu cần cập nhật (và có thể đã được xử lý nếu AutoUpdateProfiles bật).");
            }
        }
    }

    // Sử dụng trực tiếp class từ Models thay vì định nghĩa lại
    // Class đã được định nghĩa trong Models/UpdateCheckSettings.cs
}