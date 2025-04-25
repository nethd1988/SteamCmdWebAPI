using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class UpdateCheckService : BackgroundService
    {
        private readonly ILogger<UpdateCheckService> _logger;
        private readonly SteamApiService _steamApiService;
        private readonly ProfileService _profileService;
        private readonly SteamCmdService _steamCmdService;
        private TimeSpan _checkInterval = TimeSpan.FromMinutes(10);
        private bool _enabled = true;
        private bool _autoUpdateProfiles = true;
        private readonly string _settingsFilePath;
        private readonly object _settingsLock = new object();

        public UpdateCheckService(
            ILogger<UpdateCheckService> logger,
            SteamApiService steamApiService,
            ProfileService profileService,
            SteamCmdService steamCmdService)
        {
            _logger = logger;
            _steamApiService = steamApiService;
            _profileService = profileService;
            _steamCmdService = steamCmdService;

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

            // Đợi khởi động cho tất cả dịch vụ khác
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

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

            // Kiểm tra cập nhật cho mỗi profile
            var updateResults = await _steamApiService.CheckUpdatesForProfiles(profilesToCheck);
            bool anyUpdates = false;

            foreach (var profile in profilesToCheck)
            {
                // Bỏ qua nếu AppID không hợp lệ
                if (string.IsNullOrEmpty(profile.AppID) || !updateResults.ContainsKey(profile.AppID))
                {
                    continue;
                }

                bool hasUpdate = updateResults[profile.AppID];
                if (hasUpdate)
                {
                    _logger.LogInformation("Phát hiện cập nhật cho profile {0} (AppID: {1}). Đang thêm vào hàng đợi cập nhật...",
                        profile.Name, profile.AppID);

                    // Thêm vào hàng đợi cập nhật
                    await _steamCmdService.QueueProfileForUpdate(profile.Id);
                    anyUpdates = true;
                }
                else
                {
                    _logger.LogInformation("Không có cập nhật mới cho profile {0} (AppID: {1})",
                        profile.Name, profile.AppID);
                }
            }

            if (!anyUpdates)
            {
                _logger.LogInformation("Không phát hiện cập nhật mới cho bất kỳ profile nào");
            }
        }
    }
}