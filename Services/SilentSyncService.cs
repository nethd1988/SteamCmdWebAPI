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
        private readonly Timer _autoSyncTimer;
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
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 phút timeout cho các request lớn

            // Thiết lập timer cho việc đồng bộ âm thầm
            _autoSyncTimer = new Timer(async _ => await SyncAllProfilesAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15));
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
                // Lấy cài đặt server
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (!serverSettings.EnableServerSync || string.IsNullOrEmpty(serverSettings.ServerAddress))
                {
                    _isSyncing = false;
                    return (false, "Đồng bộ với server chưa được bật hoặc chưa cấu hình");
                }

                // Lấy tất cả profiles
                var profiles = await _profileService.GetAllProfiles();
                if (profiles.Count == 0)
                {
                    _isSyncing = false;
                    return (false, "Không có profiles để đồng bộ");
                }

                // Gửi full sync
                var (success, message) = await SendFullSyncAsync(serverSettings.ServerAddress, profiles, serverSettings.ServerPort);

                // Cập nhật thời gian đồng bộ
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

        public async Task<(bool Success, string Message)> SyncProfileAsync(int profileId)
        {
            try
            {
                // Lấy cài đặt server
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (!serverSettings.EnableServerSync || string.IsNullOrEmpty(serverSettings.ServerAddress))
                {
                    return (false, "Đồng bộ với server chưa được bật hoặc chưa cấu hình");
                }

                // Lấy profile
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    return (false, $"Không tìm thấy profile với ID {profileId}");
                }

                // Gửi profile
                var (success, message) = await SendProfileAsync(serverSettings.ServerAddress, profile, serverSettings.ServerPort);
                return (success, message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profile {ProfileId}", profileId);
                return (false, $"Lỗi: {ex.Message}");
            }
        }

        private async Task<(bool Success, string Message)> SendProfileAsync(string serverAddress, SteamCmdProfile profile, int port = 61188)
        {
            try
            {
                // Chuẩn bị URL
                string url = $"http://{serverAddress}:{port}/api/silentsync/profile";
                _logger.LogInformation("Gửi profile {ProfileName} (ID: {ProfileId}) đến {Url}",
                    profile.Name, profile.Id, url);

                // Tạo request
                var content = new StringContent(
                    JsonSerializer.Serialize(profile),
                    Encoding.UTF8,
                    "application/json");

                // Gửi request
                var response = await _httpClient.PostAsync(url, content);

                // Đọc và xử lý response
                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Đã gửi profile {ProfileName} (ID: {ProfileId}) thành công",
                        profile.Name, profile.Id);
                    return (true, $"Đã gửi profile {profile.Name} thành công");
                }
                else
                {
                    _logger.LogWarning("Lỗi khi gửi profile {ProfileName} (ID: {ProfileId}). Status: {StatusCode}, Response: {Response}",
                        profile.Name, profile.Id, response.StatusCode, responseContent);
                    return (false, $"Lỗi: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi profile {ProfileName} (ID: {ProfileId})",
                    profile.Name, profile.Id);
                return (false, $"Lỗi kết nối: {ex.Message}");
            }
        }

        private async Task<(bool Success, string Message)> SendBatchAsync(string serverAddress, List<SteamCmdProfile> profiles, int port = 61188)
        {
            try
            {
                // Chuẩn bị URL
                string url = $"http://{serverAddress}:{port}/api/silentsync/batch";
                _logger.LogInformation("Gửi batch {Count} profiles đến {Url}", profiles.Count, url);

                // Tạo request
                var content = new StringContent(
                    JsonSerializer.Serialize(profiles),
                    Encoding.UTF8,
                    "application/json");

                // Gửi request
                var response = await _httpClient.PostAsync(url, content);

                // Đọc và xử lý response
                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Đã gửi batch {Count} profiles thành công", profiles.Count);
                    return (true, $"Đã gửi {profiles.Count} profiles thành công");
                }
                else
                {
                    _logger.LogWarning("Lỗi khi gửi batch profiles. Status: {StatusCode}, Response: {Response}",
                        response.StatusCode, responseContent);
                    return (false, $"Lỗi: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi batch profiles");
                return (false, $"Lỗi kết nối: {ex.Message}");
            }
        }

        private async Task<(bool Success, string Message)> SendFullSyncAsync(string serverAddress, List<SteamCmdProfile> profiles, int port = 61188)
        {
            try
            {
                // Chuẩn bị URL
                string url = $"http://{serverAddress}:{port}/api/silentsync/full";
                _logger.LogInformation("Gửi full sync {Count} profiles đến {Url}", profiles.Count, url);

                // Tạo request - sử dụng JsonSerializer để chuyển đổi
                string json = JsonSerializer.Serialize(profiles);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                // Gửi request
                var response = await _httpClient.PostAsync(url, content);

                // Đọc và xử lý response
                string responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Đã gửi full sync {Count} profiles thành công", profiles.Count);
                    return (true, $"Đã đồng bộ {profiles.Count} profiles thành công");
                }
                else
                {
                    _logger.LogWarning("Lỗi khi gửi full sync. Status: {StatusCode}, Response: {Response}",
                        response.StatusCode, responseContent);
                    return (false, $"Lỗi: {response.StatusCode} - {responseContent}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi full sync");
                return (false, $"Lỗi kết nối: {ex.Message}");
            }
        }
    }
}