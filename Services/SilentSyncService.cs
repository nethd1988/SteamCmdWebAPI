using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class SilentSyncService
    {
        private readonly ILogger<SilentSyncService> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ProfileService _profileService;
        private readonly HttpClient _httpClient;
        private readonly object _syncLock = new object();
        private bool _isSyncing = false;

        public SilentSyncService(
            ILogger<SilentSyncService> logger,
            ServerSettingsService serverSettingsService,
            ProfileService profileService)
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService;
            _profileService = profileService;
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5);
        }

        public async Task<(bool Success, string Message)> SyncAllProfilesAsync()
        {
            if (_isSyncing)
            {
                return (false, "Quá trình đồng bộ đang diễn ra");
            }

            lock (_syncLock)
            {
                if (_isSyncing) return (false, "Quá trình đồng bộ đang diễn ra");
                _isSyncing = true;
            }

            try
            {
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (!serverSettings.EnableServerSync || string.IsNullOrEmpty(serverSettings.ServerAddress))
                {
                    _isSyncing = false;
                    return (false, "Đồng bộ với server chưa được bật hoặc chưa cấu hình");
                }

                var profiles = await _profileService.GetAllProfiles();
                if (profiles.Count == 0)
                {
                    _isSyncing = false;
                    return (false, "Không có profiles để đồng bộ");
                }

                var (success, message) = await SendFullSyncAsync(serverSettings.ServerAddress, profiles, serverSettings.ServerPort);

                if (success)
                {
                    await _serverSettingsService.UpdateLastSyncTimeAsync();
                }

                _isSyncing = false;
                return (success, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ tất cả profiles");
                _isSyncing = false;
                return (false, $"Lỗi: {ex.Message}");
            }
        }

        private async Task<(bool Success, string Message)> SendFullSyncAsync(string serverAddress, List<SteamCmdProfile> profiles, int port = 61188)
        {
            try
            {
                string url = $"http://{serverAddress}:{port}/api/sync/profiles";
                _logger.LogInformation("Gửi đồng bộ {Count} profiles đến {Url}", profiles.Count, url);

                string json = JsonSerializer.Serialize(profiles);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);
                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Đã gửi đồng bộ {Count} profiles thành công", profiles.Count);
                    return (true, $"Đã đồng bộ {profiles.Count} profiles thành công");
                }
                else
                {
                    _logger.LogWarning("Lỗi khi gửi đồng bộ. Status: {StatusCode}, Response: {Response}",
                        response.StatusCode, responseContent);
                    return (false, $"Lỗi: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi đồng bộ");
                return (false, $"Lỗi kết nối: {ex.Message}");
            }
        }
    }
}