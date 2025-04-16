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
                return new SteamCmdWebAPI.Models.AutoRunSettings { 
                    AutoRunEnabled = false, 
                    AutoRunIntervalHours = 12, // Mặc định 12 giờ
                    AutoRunInterval = "daily",
                    ScheduledHour = 7 
                };
            }

            try
            {
                string json = await File.ReadAllTextAsync(_configPath);
                var settings = JsonConvert.DeserializeObject<SteamCmdWebAPI.Models.AutoRunSettings>(json);
                
                // Nếu cài đặt là null hoặc khoảng thời gian không hợp lệ
                if (settings == null)
                {
                    return new SteamCmdWebAPI.Models.AutoRunSettings { 
                        AutoRunEnabled = false, 
                        AutoRunIntervalHours = 12, 
                        AutoRunInterval = "daily",
                        ScheduledHour = 7 
                    };
                }
                
                // Kiểm tra và đảm bảo khoảng thời gian hợp lệ (1-48 giờ)
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;
                
                // Đảm bảo giờ chạy hợp lệ (1-24h)
                if (settings.ScheduledHour < 1) settings.ScheduledHour = 1;
                if (settings.ScheduledHour > 24) settings.ScheduledHour = 24;
                
                _logger.LogInformation("Đã đọc settings từ {0}", _configPath);
                return settings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc file settings.json tại {0}", _configPath);
                return new SteamCmdWebAPI.Models.AutoRunSettings { 
                    AutoRunEnabled = false, 
                    AutoRunIntervalHours = 12, 
                    AutoRunInterval = "daily",
                    ScheduledHour = 7 
                };
            }
        }

        public async Task SaveSettingsAsync(SteamCmdWebAPI.Models.AutoRunSettings settings)
        {
            try
            {
                // Kiểm tra giá trị hợp lệ trước khi lưu
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;
                
                if (settings.ScheduledHour < 1) settings.ScheduledHour = 1;
                if (settings.ScheduledHour > 24) settings.ScheduledHour = 24;
                
                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                await File.WriteAllTextAsync(_configPath, updatedJson);
                _logger.LogInformation("Đã lưu settings vào {0}", _configPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu settings vào {0}", _configPath);
                throw;
            }
        }
    }
}