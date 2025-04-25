using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using SteamCmdWebAPI.Models;
using System.Collections.Generic; // Added for Dictionary

namespace SteamCmdWebAPI.Services
{
    public class UpdateCheckService : BackgroundService
    {
        private readonly ILogger<UpdateCheckService> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService; // Inject SteamCmdService
        private TimeSpan _checkInterval = TimeSpan.FromMinutes(10);
        private bool _enabled = true;
        private bool _autoUpdateProfiles = true;
        private readonly string _settingsFilePath;
        private readonly object _settingsLock = new object();

        public UpdateCheckService(
            ILogger<UpdateCheckService> logger,
            SteamApiService steamApiService,
            ProfileService profileService,
            SteamCmdService steamCmdService) // Inject SteamCmdService
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService; // Assign injected service

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
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải cài đặt kiểm tra cập nhật");
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt kiểm tra cập nhật");
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

            // Đợi khởi động cho tất cả dịch vụ khác (tăng thời gian chờ để đảm bảo SteamCmdService sẵn sàng)
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); // Increased delay

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
                        _logger.LogError(ex, "Lỗi trong quá trình kiểm tra cập nhật tự động");
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

            // Tải tất cả profiles có AutoRun = true nếu cài đặt AutoUpdateProfiles = true
            // Nếu không thì tải tất cả profiles
            var profiles = await _profileService.GetAllProfiles();
            var profilesToCheck = autoUpdateEnabled
                ? profiles.Where(p => p.AutoRun).ToList()
                : profiles;

            if (!profilesToCheck.Any())
            {
                _logger.LogInformation("Không có profile nào để kiểm tra cập nhật");
                return;
            }

            _logger.LogInformation("Tìm thấy {0} profile để kiểm tra", profilesToCheck.Count);

            bool anyUpdatesFound = false;

            foreach (var profile in profilesToCheck)
            {
                if (string.IsNullOrEmpty(profile.AppID) || string.IsNullOrEmpty(profile.InstallDirectory))
                {
                    _logger.LogWarning("Bỏ qua profile {0} do thiếu AppID hoặc Thư mục cài đặt.", profile.Name);
                    continue;
                }

                _logger.LogInformation("Kiểm tra cập nhật cho profile: {0} (AppID: {1})", profile.Name, profile.AppID);

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
                        // Nếu không đọc được manifest hoặc ChangeNumber, coi như cần cập nhật/cài đặt lần đầu
                        localChangeNumber = -1; // Đảm bảo giá trị nhỏ hơn bất kỳ ChangeNumber hợp lệ nào
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi đọc manifest cục bộ cho profile {0} (AppID: {1})", profile.Name, profile.AppID);
                    localChangeNumber = -1; // Coi như cần kiểm tra/cài đặt nếu có lỗi đọc manifest
                }


                // 2. Lấy thông tin mới nhất từ Steam API
                long latestApiChangeNumber = -1;
                var latestAppInfo = await _steamApiService.GetAppUpdateInfo(profile.AppID); // forceRefresh = false by default, uses cache if recent

                if (latestAppInfo != null)
                {
                    latestApiChangeNumber = latestAppInfo.ChangeNumber;
                    _logger.LogInformation("Steam API cho AppID {1} ({0}) có ChangeNumber mới nhất: {2}", profile.Name, profile.AppID, latestApiChangeNumber);

                    // Log thời gian cập nhật cuối cùng từ API
                    if (latestAppInfo.LastUpdateDateTime.HasValue)
                    {
                        _logger.LogInformation("Steam API cho AppID {1} ({0}) được cập nhật lần cuối vào: {2}", profile.Name, profile.AppID, latestAppInfo.LastUpdateDateTime.Value.ToLocalTime());
                    }
                    else
                    {
                        _logger.LogInformation("Steam API không cung cấp thông tin thời gian cập nhật cuối cùng cho AppID {1} ({0})", profile.Name, profile.AppID);
                    }
                }
                else
                {
                    _logger.LogWarning("Không thể lấy thông tin Steam API cho AppID {1} ({0}). Không thể kiểm tra cập nhật chính xác.", profile.Name, profile.AppID);
                    // Không thể kiểm tra chính xác nếu không lấy được API info. Bỏ qua profile này cho lần kiểm tra này.
                    continue;
                }


                // 3. So sánh ChangeNumber để xác định có cập nhật hay không
                bool needsUpdate = false;
                if (latestApiChangeNumber > localChangeNumber)
                {
                    _logger.LogInformation("Phát hiện cập nhật cho profile {0} (AppID: {1}): API ChangeNumber ({2}) > Local ChangeNumber ({3})",
                        profile.Name, profile.AppID, latestApiChangeNumber, localChangeNumber);
                    needsUpdate = true;
                }
                else
                {
                    _logger.LogInformation("Không có cập nhật mới cho profile {0} (AppID: {1}): API ChangeNumber ({2}) <= Local ChangeNumber ({3})",
                       profile.Name, profile.AppID, latestApiChangeNumber, localChangeNumber);
                }


                // 4. Nếu cần cập nhật và AutoUpdateProfiles được bật, thêm vào hàng đợi
                if (needsUpdate)
                {
                    anyUpdatesFound = true; // Mark that at least one update was found

                    if (autoUpdateEnabled)
                    {
                        _logger.LogInformation("AutoUpdateProfiles được bật. Đang thêm profile {0} (ID: {1}) vào hàng đợi cập nhật...",
                            profile.Name, profile.Id);
                        await _steamCmdService.QueueProfileForUpdate(profile.Id);
                    }
                    else
                    {
                        _logger.LogInformation("AutoUpdateProfiles đang tắt. Không tự động thêm profile {0} vào hàng đợi.", profile.Name);
                        // Có thể thêm logic thông báo cho người dùng rằng có cập nhật nhưng không tự động chạy
                    }
                }
            } // End foreach profile

            if (!anyUpdatesFound)
            {
                _logger.LogInformation("Không phát hiện cập nhật mới cho bất kỳ profile nào cần kiểm tra.");
            }
        }
    }
}
