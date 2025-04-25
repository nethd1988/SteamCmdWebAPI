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
            string dataDir = Path.Combine(currentDir, "data");

            // Kiểm tra và tạo thư mục data nếu chưa tồn tại
            if (!Directory.Exists(dataDir))
            {
                try
                {
                    Directory.CreateDirectory(dataDir);
                    _logger.LogInformation("SettingsService: Đã tạo thư mục data tại {DataDirectory}", dataDir);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SettingsService: Lỗi khi tạo thư mục data tại {DataDirectory}", dataDir);
                    // Tiếp tục mà không ném ngoại lệ, nhưng log lỗi. Việc tạo file settings có thể sẽ thất bại sau đó.
                }
            }

            _configPath = Path.Combine(dataDir, "settings.json");

            // Log thông báo về sự tồn tại của tệp cài đặt lúc khởi tạo dịch vụ (không tạo file ở đây nữa)
            if (!File.Exists(_configPath))
            {
                _logger.LogWarning("SettingsService: File settings.json không tồn tại tại {ConfigPath} khi khởi tạo dịch vụ. Sẽ tạo file khi tải hoặc lưu lần đầu.", _configPath);
            }
            else
            {
                _logger.LogInformation("SettingsService: File settings.json tồn tại tại {ConfigPath} khi khởi tạo dịch vụ.", _configPath);
            }
        }

        public async Task<SteamCmdWebAPI.Models.AutoRunSettings> LoadSettingsAsync()
        {
            _logger.LogInformation("SettingsService: Đang cố gắng tải settings từ {ConfigPath}", _configPath);

            if (!File.Exists(_configPath))
            {
                _logger.LogInformation("SettingsService: File settings.json không tồn tại tại {ConfigPath}. Tạo file mới với cài đặt mặc định.", _configPath);
                // Tạo file với cài đặt mặc định nếu không tồn tại
                var defaultSettings = new SteamCmdWebAPI.Models.AutoRunSettings
                {
                    AutoRunEnabled = false, // Mặc định TẮT tự động chạy
                    AutoRunIntervalHours = 12,
                    AutoRunInterval = "daily"
                };
                try
                {
                    // Đảm bảo thư mục cha tồn tại trước khi ghi file
                    var directory = Path.GetDirectoryName(_configPath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    string defaultJson = JsonConvert.SerializeObject(defaultSettings, Formatting.Indented);
                    await File.WriteAllTextAsync(_configPath, defaultJson);
                    _logger.LogInformation("SettingsService: Đã tạo file settings.json mặc định tại {ConfigPath}", _configPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SettingsService: Lỗi khi tạo file settings.json mặc định tại {ConfigPath}", _configPath);
                    // Tiếp tục trả về mặc định trong bộ nhớ ngay cả khi không thể tạo file
                }

                // Trả về cài đặt mặc định trong bộ nhớ
                return defaultSettings;
            }

            try
            {
                string json = await File.ReadAllTextAsync(_configPath);

                // Kiểm tra nội dung tệp có rỗng không trước khi Deserialize
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger.LogWarning("SettingsService: Nội dung file settings.json tại {ConfigPath} bị rỗng. Trả về cài đặt mặc định.", _configPath);
                    return new SteamCmdWebAPI.Models.AutoRunSettings
                    {
                        AutoRunEnabled = false,
                        AutoRunIntervalHours = 12,
                        AutoRunInterval = "daily"
                    };
                }

                var settings = JsonConvert.DeserializeObject<SteamCmdWebAPI.Models.AutoRunSettings>(json);

                // Nếu cài đặt là null sau khi Deserialize (ví dụ: tệp chỉ chứa "{}")
                if (settings == null)
                {
                    _logger.LogWarning("SettingsService: Deserialize settings từ {ConfigPath} trả về null. Tệp có thể không đúng định dạng. Trả về cài đặt mặc định.", _configPath);
                    return new SteamCmdWebAPI.Models.AutoRunSettings
                    {
                        AutoRunEnabled = false,
                        AutoRunIntervalHours = 12,
                        AutoRunInterval = "daily"
                    };
                }

                // Chuyển đổi từ cài đặt cũ sang mới nếu cần và kiểm tra giá trị hợp lệ
                // Logic này giống như trong mã cũ, giữ nguyên
                if (settings.AutoRunIntervalHours <= 0)
                {
                    switch (settings.AutoRunInterval?.ToLower())
                    {
                        case "daily": settings.AutoRunIntervalHours = 24; break;
                        case "weekly": settings.AutoRunIntervalHours = 168; break;
                        case "monthly": settings.AutoRunIntervalHours = 720; break;
                        default: settings.AutoRunIntervalHours = 12; break;
                    }
                }
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;


                _logger.LogInformation("SettingsService: Đã tải và đọc settings thành công từ {ConfigPath}. AutoRunEnabled={AutoRunEnabled}, AutoRunIntervalHours={AutoRunIntervalHours}",
                                       _configPath, settings.AutoRunEnabled, settings.AutoRunIntervalHours);
                return settings;
            }
            catch (JsonException jsonEx) // Bắt lỗi Deserialize JSON cụ thể
            {
                _logger.LogError(jsonEx, "SettingsService: Lỗi Deserialize JSON từ file settings.json tại {ConfigPath}. Tệp có thể bị hỏng hoặc sai định dạng. Trả về cài đặt mặc định.", _configPath);
                return new SteamCmdWebAPI.Models.AutoRunSettings
                {
                    AutoRunEnabled = false,
                    AutoRunIntervalHours = 12,
                    AutoRunInterval = "daily"
                };
            }
            catch (Exception ex) // Bắt các lỗi đọc file khác
            {
                _logger.LogError(ex, "SettingsService: Lỗi khi đọc file settings.json tại {ConfigPath}. Trả về cài đặt mặc định.", _configPath);
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
            _logger.LogInformation("SettingsService: Đang cố gắng lưu settings vào {ConfigPath}", _configPath);
            try
            {
                var directory = Path.GetDirectoryName(_configPath);
                if (!Directory.Exists(directory))
                {
                    try
                    {
                        Directory.CreateDirectory(directory);
                        _logger.LogInformation("SettingsService: Đã tạo thư mục {Directory} để lưu settings.", directory);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SettingsService: Lỗi khi tạo thư mục {Directory} để lưu settings.", directory);
                        throw; // Ném lỗi để controller/page biết việc lưu thất bại
                    }
                }

                // Kiểm tra giá trị hợp lệ trước khi lưu
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;

                // Cập nhật chuỗi AutoRunInterval cho tương thích ngược
                settings.AutoRunInterval = ConvertIntervalHoursToString(settings.AutoRunIntervalHours);

                // Ghi log trước khi lưu để kiểm tra
                _logger.LogInformation("SettingsService: Đang lưu settings: AutoRunEnabled={AutoRunEnabled}, AutoRunIntervalHours={AutoRunIntervalHours}",
                                        settings.AutoRunEnabled, settings.AutoRunIntervalHours);

                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                await File.WriteAllTextAsync(_configPath, updatedJson);

                // Kiểm tra lại file đã được tạo
                bool fileExists = File.Exists(_configPath);
                _logger.LogInformation("SettingsService: Đã hoàn thành lưu settings vào {ConfigPath}, file exists: {FileExists}", _configPath, fileExists);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SettingsService: Lỗi khi lưu settings vào {ConfigPath}: {ErrorMessage}", _configPath, ex.Message);
                throw; // Ném lỗi để controller/page biết việc lưu thất bại
            }
        }

        // Helper method để chuyển đổi giờ thành chuỗi tương thích ngược
        private string ConvertIntervalHoursToString(int hours)
        {
            if (hours >= 1 && hours <= 24) return "daily";
            if (hours > 24 && hours <= 168) return "weekly"; // 7*24
            if (hours > 168 && hours <= 730) return "monthly"; // approx 30*24
            if (hours > 730) return "monthly"; // catch all larger intervals
            return "daily"; // default fallback
        }
    }
}