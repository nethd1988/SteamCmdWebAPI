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

        public ServerSettingsService(ILogger<ServerSettingsService> logger)
        {
            _logger = logger;
            
            // Lấy thư mục gốc của dự án
            string projectDir = AppContext.BaseDirectory;
            int maxDepth = 10;
            int depth = 0;

            while (projectDir != null && !File.Exists(Path.Combine(projectDir, "SteamCmdWebAPI.csproj")) && depth < maxDepth)
            {
                projectDir = Directory.GetParent(projectDir)?.FullName;
                depth++;
            }

            if (projectDir == null || depth >= maxDepth)
            {
                throw new Exception($"Không thể tìm thấy thư mục gốc của dự án. BaseDirectory: {AppContext.BaseDirectory}");
            }

            // Lưu file server_settings.json trong thư mục data
            string dataDir = Path.Combine(projectDir, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
            }

            _settingsFilePath = Path.Combine(dataDir, "server_settings.json");
            _logger.LogInformation("ServerSettingsService khởi tạo với _settingsFilePath: {0}", _settingsFilePath);
        }

        public async Task<ServerSettings> LoadSettingsAsync()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    string json = await File.ReadAllTextAsync(_settingsFilePath);
                    var settings = JsonConvert.DeserializeObject<ServerSettings>(json);
                    return settings ?? new ServerSettings();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc cài đặt server: {Message}", ex.Message);
            }
            
            // Trả về cài đặt mặc định nếu không đọc được
            return new ServerSettings();
        }

        public async Task SaveSettingsAsync(ServerSettings settings)
        {
            try
            {
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