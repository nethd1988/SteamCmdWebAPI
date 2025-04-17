using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class SettingsService
    {
        private readonly string _configPath;
        private readonly ILogger<SettingsService> _logger;

        public SettingsService(ILogger<SettingsService> logger)
        {
            _logger = logger;
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            // Lưu file profiles.json trong thư mục data
            string dataDir = Path.Combine(currentDir, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
            }

            _configPath = Path.Combine(dataDir, "settings.json");
            if (!File.Exists(_configPath))
            {
                _logger.LogError("File setting not exist in :", _configPath);
            }
        }

        public async Task<SteamCmdWebAPI.Models.AutoRunSettings> LoadSettingsAsync()
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogInformation("File settings.json không tồn tại tại {0}. Trả về cài đặt mặc định.", _configPath);
                return new SteamCmdWebAPI.Models.AutoRunSettings
                {
                    AutoRunEnabled = false,
                    AutoRunIntervalHours = 12, // Mặc định 12 giờ
                    AutoRunInterval = "daily"
                };
            }

            try
            {
                string json = await File.ReadAllTextAsync(_configPath);
                var settings = JsonConvert.DeserializeObject<SteamCmdWebAPI.Models.AutoRunSettings>(json);

                // Nếu cài đặt là null hoặc khoảng thời gian không hợp lệ
                if (settings == null)
                {
                    return new SteamCmdWebAPI.Models.AutoRunSettings
                    {
                        AutoRunEnabled = false,
                        AutoRunIntervalHours = 12,
                        AutoRunInterval = "daily"
                    };
                }

                // Chuyển đổi từ cài đặt cũ sang mới nếu cần
                if (settings.AutoRunIntervalHours <= 0)
                {
                    // Nếu dùng cài đặt cũ, chuyển đổi sang giờ
                    switch (settings.AutoRunInterval?.ToLower())
                    {
                        case "daily":
                            settings.AutoRunIntervalHours = 24;
                            break;
                        case "weekly":
                            settings.AutoRunIntervalHours = 168; // 7 * 24
                            break;
                        case "monthly":
                            settings.AutoRunIntervalHours = 720; // 30 * 24 (gần đúng)
                            break;
                        default:
                            settings.AutoRunIntervalHours = 12; // Mặc định
                            break;
                    }
                }

                // Kiểm tra và đảm bảo khoảng thời gian hợp lệ (1-48 giờ)
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;

                _logger.LogInformation("Đã đọc settings từ {0}", _configPath);
                return settings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc file settings.json tại {0}", _configPath);
                return new SteamCmdWebAPI.Models.AutoRunSettings
                {
                    AutoRunEnabled = false,
                    AutoRunIntervalHours = 12,
                    AutoRunInterval = "daily"
                };
            }
        }

        public async Task SaveSettingsAsync(SteamCmdWebAPI.Models.AutoRunSettings settings)
        {
            try
            {
                // Kiểm tra thư mục tồn tại
                var directory = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogInformation("Đã tạo thư mục {0}", directory);
                }

                // Kiểm tra giá trị hợp lệ trước khi lưu
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;

                // Cập nhật chuỗi AutoRunInterval cho tương thích ngược
                settings.AutoRunInterval = ConvertIntervalHoursToString(settings.AutoRunIntervalHours);

                // Ghi log trước khi lưu để kiểm tra
                _logger.LogInformation("Đang lưu settings: AutoRunEnabled={0}, AutoRunIntervalHours={1} vào {2}",
                    settings.AutoRunEnabled, settings.AutoRunIntervalHours, _configPath);

                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                await File.WriteAllTextAsync(_configPath, updatedJson);

                // Kiểm tra lại file đã được tạo
                bool fileExists = File.Exists(_configPath);
                _logger.LogInformation("Đã lưu settings vào {0}, file exists: {1}", _configPath, fileExists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu settings vào {0}: {1}", _configPath, ex.Message);
                throw;
            }
        }

        // Helper method để chuyển đổi giờ thành chuỗi tương thích ngược
        private string ConvertIntervalHoursToString(int hours)
        {
            if (hours <= 24) return "daily";
            if (hours <= 168) return "weekly";
            return "monthly";
        }
    }
}