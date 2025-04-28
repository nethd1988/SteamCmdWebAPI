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
        // Thêm DependencyManagerService [cite: 1]
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
            // Inject DependencyManagerService [cite: 2]
            DependencyManagerService dependencyManagerService)
        {
            _logger = logger;
            _steamApiService = steamApiService; // [cite: 3]
            _profileService = profileService; // [cite: 4]
            _steamCmdService = steamCmdService;
            // Gán dependency [cite: 5]
            _dependencyManagerService = dependencyManagerService;

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            _settingsFilePath = Path.Combine(dataDir, "update_check_settings.json");

            // Các giá trị mặc định theo hình
            _checkInterval = TimeSpan.FromMinutes(10); // 10 phút
            _enabled = true; // Bật tính năng
            _autoUpdateProfiles = true; // Bật tự động cập nhật

            LoadSettings();
        }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<Models.UpdateCheckSettings>(json);

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

            // ===== Bắt đầu thay đổi logic tạo danh sách kiểm tra =====
            // Trước khi duyệt qua các profile, lấy tất cả phụ thuộc [cite: 26]
            var allDependencies = await _dependencyManagerService.GetAllDependenciesAsync();

            // Tạo danh sách các app cần kiểm tra (cả chính và phụ thuộc) [cite: 27]
            var profilesToCheck = new List<SteamCmdProfile>();
            // var additionalAppsToCheck = new List<(int ProfileId, string AppId, string AppName)>(); // Không dùng đến [cite: 28]

            foreach (var profile in profiles)
            {
                // Thêm profile vào danh sách kiểm tra chính [cite: 29]
                profilesToCheck.Add(profile);

                // Kiểm tra các app phụ thuộc [cite: 30]
                var dependency = allDependencies.FirstOrDefault(d => d.ProfileId == profile.Id);
                if (dependency != null && dependency.DependentApps.Any()) // [cite: 30]
                {
                    foreach (var app in dependency.DependentApps) // [cite: 31]
                    {
                        // Tạo profile ảo cho app phụ thuộc để kiểm tra cập nhật [cite: 31, 32]
                        var virtualProfile = new SteamCmdProfile
                        {
                            Id = profile.Id, // Giữ ID của profile gốc để liên kết
                            Name = $"{profile.Name} - {app.Name}", // Tên gợi nhớ
                            AppID = app.AppId, // AppID của app phụ thuộc
                            InstallDirectory = profile.InstallDirectory // Thư mục cài đặt của profile gốc
                        };

                        profilesToCheck.Add(virtualProfile); // [cite: 33]
                    }
                }
            }

            if (!profilesToCheck.Any()) // [cite: 34]
            {
                _logger.LogInformation("Không có profile hoặc app nào để kiểm tra cập nhật"); // [cite: 34]
                return; // [cite: 34]
            }

            _logger.LogInformation("Tìm thấy {0} profile/app để kiểm tra", profilesToCheck.Count); // [cite: 35]
            bool anyUpdatesFound = false; // [cite: 35]
            // ===== Kết thúc thay đổi logic tạo danh sách kiểm tra =====


            // Vòng lặp chính giờ sẽ duyệt qua profilesToCheck (bao gồm cả profile gốc và ảo)
            foreach (var profile in profilesToCheck) // Sử dụng danh sách mới
            {
                _logger.LogInformation("--- Đang xử lý profile/app '{ProfileName}' (ID Profile gốc: {ProfileId}, AppID: {AppId}) ---",
                                     profile.Name, profile.Id, profile.AppID);

                if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                {
                    _logger.LogWarning("Bỏ qua '{0}' (ID Profile gốc: {1}) do thiếu AppID hoặc Thư mục cài đặt.", profile.Name, profile.Id);
                    continue;
                }

                try // Thêm try-catch cho từng profile/app
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

                    // Cập nhật SizeOnDisk từ manifest nếu có thể (Giữ nguyên logic này)
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

                    // ===== Bắt đầu thay đổi logic xử lý khi phát hiện cập nhật =====
                    if (needsUpdate)
                    {
                        anyUpdatesFound = true; // [cite: 12] Đánh dấu rằng có ít nhất 1 profile/app cần cập nhật

                        // Xác định xem đây là app chính hay app phụ thuộc [cite: 13]
                        // Lưu ý: Sử dụng profile.Id (ID của profile gốc) để tra cứu dependency
                        var dependency = await _dependencyManagerService.GetDependencyByProfileIdAsync(profile.Id);
                        // App chính là khi không có dependency hoặc AppID hiện tại trùng với MainAppId
                        bool isMainApp = dependency == null || dependency.MainAppId == profile.AppID; // [cite: 14]

                        if (isMainApp)
                        {
                            // Nếu là app chính, xử lý như profile bình thường
                            if (autoUpdateEnabled)
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles được bật. Đang thêm profile '{0}' (ID: {1}, AppID chính: {2}) VÀO HÀNG ĐỢI cập nhật...",
                                    profile.Name, profile.Id, profile.AppID); // [cite: 15]

                                // Sử dụng ID của profile gốc để thêm vào hàng đợi
                                bool queueSuccess = await _steamCmdService.QueueProfileForUpdate(profile.Id); // [cite: 16]

                                if (queueSuccess)
                                {
                                    _logger.LogInformation("--> Đã thêm profile '{0}' (ID: {1}) vào hàng đợi thành công.", profile.Name, profile.Id); // [cite: 17]
                                }
                                else
                                {
                                    _logger.LogError("--> LỖI: Không thể thêm profile '{0}' (ID: {1}) vào hàng đợi.", profile.Name, profile.Id); // [cite: 18]
                                }
                            }
                            else
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles đang tắt. KHÔNG tự động thêm profile '{0}' (ID: {1}) vào hàng đợi dù phát hiện cập nhật.", profile.Name, profile.Id); // [cite: 19]
                            }
                        }
                        else // Nếu là app phụ thuộc
                        {
                            // Nếu là app phụ thuộc, đánh dấu app cần cập nhật
                            await _dependencyManagerService.MarkAppForUpdateAsync(profile.AppID); // [cite: 20]

                            if (autoUpdateEnabled)
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles được bật. Đang thiết lập App phụ thuộc ID: {0} để cập nhật trong profile '{1}' (ID gốc: {2})...",
                                    profile.AppID, profile.Name, profile.Id); // [cite: 21]

                                // Gọi hàm cập nhật app cụ thể, sử dụng ID profile gốc và AppID phụ thuộc
                                bool queueSuccess = await _steamCmdService.RunSpecificAppAsync(profile.Id, profile.AppID); // [cite: 22]

                                if (queueSuccess)
                                {
                                    _logger.LogInformation("--> Đã thiết lập cập nhật App ID {0} thành công.", profile.AppID); // [cite: 23]
                                }
                                else
                                {
                                    _logger.LogError("--> LỖI: Không thể cập nhật App ID {0} của profile '{1}' (ID gốc: {2}).", profile.AppID, profile.Name, profile.Id); // [cite: 24]
                                }
                            }
                            else
                            {
                                _logger.LogInformation("--> AutoUpdateProfiles đang tắt. KHÔNG tự động cập nhật App ID {0} của profile '{1}' (ID gốc: {2}) dù phát hiện cập nhật.",
                                    profile.AppID, profile.Name, profile.Id); // [cite: 25]
                            }
                        }
                    }
                    // ===== Kết thúc thay đổi logic xử lý khi phát hiện cập nhật =====
                    else
                    {
                        _logger.LogInformation("Không có cập nhật mới cần xử lý cho '{0}' (AppID: {1}).", profile.Name, profile.AppID);
                    }
                }
                catch (Exception ex) // Catch block cho lỗi xử lý từng profile/app
                {
                    _logger.LogError(ex, "Lỗi khi xử lý '{0}' (ID Profile gốc: {1}, AppID: {2}) trong quá trình kiểm tra cập nhật.",
                                     profile.Name, profile.Id, profile.AppID);
                }

                _logger.LogInformation("--- Kết thúc xử lý '{ProfileName}' ---", profile.Name);

            } // Kết thúc vòng lặp foreach profile/app

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

    // Class phụ trợ để lưu/tải cài đặt (giữ nguyên)
    public class UpdateCheckSettings
    {
        public bool Enabled { get; set; } = true;
        public int IntervalMinutes { get; set; } = 10;
        public bool AutoUpdateProfiles { get; set; } = true;
    }
}