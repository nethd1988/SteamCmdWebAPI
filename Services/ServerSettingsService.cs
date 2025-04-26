using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class ServerSettingsService
    {
        private readonly ILogger<ServerSettingsService> _logger;
        private readonly string _settingsFilePath;

        // Địa chỉ server mặc định không thay đổi
        private const string DEFAULT_SERVER_ADDRESS = "idckz.ddnsfree.com";
        private const int DEFAULT_SERVER_PORT = 61188;

        public ServerSettingsService(ILogger<ServerSettingsService> logger)
        {
            _logger = logger;
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(currentDir, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
            }
            _settingsFilePath = Path.Combine(dataDir, "server_settings.json");
        }

        public async Task<ServerSettings> LoadSettingsAsync()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = await File.ReadAllTextAsync(_settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<ServerSettings>(json);

                    if (settings != null)
                    {
                        // Luôn ghi đè địa chỉ server và thiết lập EnableServerSync = true
                        settings.ServerAddress = DEFAULT_SERVER_ADDRESS;
                        settings.ServerPort = DEFAULT_SERVER_PORT;
                        settings.EnableServerSync = true;
                        return settings;
                    }
                }

                // Trả về cài đặt mặc định với EnableServerSync = true
                return new ServerSettings
                {
                    ServerAddress = DEFAULT_SERVER_ADDRESS,
                    ServerPort = DEFAULT_SERVER_PORT,
                    EnableServerSync = true,
                    ConnectionStatus = "Unknown",
                    LastSyncTime = null
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc cài đặt server: {Message}", ex.Message);
                return new ServerSettings
                {
                    ServerAddress = DEFAULT_SERVER_ADDRESS,
                    ServerPort = DEFAULT_SERVER_PORT,
                    EnableServerSync = true,
                    ConnectionStatus = "Unknown",
                    LastSyncTime = null
                };
            }
        }

        public async Task SaveSettingsAsync(ServerSettings settings)
        {
            try
            {
                // Đảm bảo luôn ghi đè địa chỉ server bằng địa chỉ mặc định
                settings.ServerAddress = DEFAULT_SERVER_ADDRESS;
                settings.ServerPort = DEFAULT_SERVER_PORT;

                string updatedJson = JsonConvert.SerializeObject(settings, Formatting.Indented);
                await File.WriteAllTextAsync(_settingsFilePath, updatedJson);
                _logger.LogInformation("Đã lưu cài đặt server vào {0}", _settingsFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt server: {Message}", ex.Message);
                throw;
            }
        }

        public async Task UpdateLastSyncTimeAsync()
        {
            try
            {
                var settings = await LoadSettingsAsync();
                settings.LastSyncTime = DateTime.Now;
                await SaveSettingsAsync(settings);
                _logger.LogInformation("Đã cập nhật thời gian đồng bộ lần cuối");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật thời gian đồng bộ: {Message}", ex.Message);
                throw;
            }
        }

        public async Task UpdateConnectionStatusAsync(string status)
        {
            try
            {
                var settings = await LoadSettingsAsync();
                settings.ConnectionStatus = status;
                await SaveSettingsAsync(settings);
                _logger.LogInformation("Đã cập nhật trạng thái kết nối: {Status}", status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật trạng thái kết nối: {Message}", ex.Message);
            }
        }
    }
}