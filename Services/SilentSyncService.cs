using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using System.Linq;

namespace SteamCmdWebAPI.Services
{
    public class SilentSyncService
    {
        // Class định nghĩa ngay trong file
        public class SyncStatusResponse
        {
            public DateTime LastSyncTime { get; set; }
            public int TotalSyncCount { get; set; }
            public int SuccessSyncCount { get; set; }
            public int FailedSyncCount { get; set; }
            public int LastSyncAddedCount { get; set; }
            public int LastSyncUpdatedCount { get; set; }
            public int LastSyncErrorCount { get; set; }
            public bool SyncEnabled { get; set; }
            public DateTime CurrentTime { get; set; }
        }

        private readonly ILogger<SilentSyncService> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ServerSyncService _serverSyncService;
        private readonly ProfileService _profileService;
        private readonly string _syncStatsFilePath;
        private SyncStatusResponse _lastSyncStatus;

        public SilentSyncService(
            ILogger<SilentSyncService> logger,
            ServerSettingsService serverSettingsService,
            ServerSyncService serverSyncService,
            ProfileService profileService)
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService;
            _serverSyncService = serverSyncService;
            _profileService = profileService;

            var dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "Data");
            var syncFolder = Path.Combine(dataFolder, "Sync");
            _syncStatsFilePath = Path.Combine(syncFolder, "sync_stats.json");

            if (!Directory.Exists(syncFolder))
            {
                Directory.CreateDirectory(syncFolder);
            }

            _lastSyncStatus = LoadSyncStatus();
        }

        public async Task<(bool success, string message)> SyncAllProfilesAsync()
        {
            try
            {
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (!serverSettings.EnableServerSync)
                {
                    return (false, "Đồng bộ server chưa được kích hoạt");
                }

                // Lấy danh sách tên profile từ server
                var profileNames = await _serverSyncService.GetProfileNamesFromServerAsync();
                if (profileNames.Count == 0)
                {
                    return (false, "Không tìm thấy profile nào trên server");
                }

                int addedCount = 0;
                int updatedCount = 0;
                int errorCount = 0;

                // Đồng bộ từng profile
                foreach (var profileName in profileNames)
                {
                    try
                    {
                        var serverProfile = await _serverSyncService.GetProfileFromServerByNameAsync(profileName);
                        if (serverProfile == null)
                        {
                            _logger.LogWarning("Không lấy được thông tin profile {ProfileName} từ server", profileName);
                            errorCount++;
                            continue;
                        }

                        var localProfiles = await _profileService.GetAllProfiles();
                        var existingProfile = localProfiles.FirstOrDefault(p => p.Name == profileName);

                        if (existingProfile != null)
                        {
                            // Cập nhật profile hiện có
                            serverProfile.Id = existingProfile.Id;
                            serverProfile.Status = existingProfile.Status;
                            serverProfile.Pid = existingProfile.Pid;
                            serverProfile.StartTime = existingProfile.StartTime;
                            serverProfile.StopTime = existingProfile.StopTime;
                            serverProfile.LastRun = existingProfile.LastRun;

                            await _profileService.UpdateProfile(serverProfile);
                            updatedCount++;
                            _logger.LogInformation("Đã cập nhật profile từ server: {ProfileName}", profileName);
                        }
                        else
                        {
                            // Thêm profile mới
                            await _profileService.AddProfileAsync(serverProfile);
                            addedCount++;
                            _logger.LogInformation("Đã thêm profile mới từ server: {ProfileName}", profileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đồng bộ profile {ProfileName}", profileName);
                        errorCount++;
                    }
                }

                // Cập nhật thống kê và lưu vào file
                _lastSyncStatus.LastSyncTime = DateTime.Now;
                _lastSyncStatus.TotalSyncCount++;
                _lastSyncStatus.SuccessSyncCount++;
                _lastSyncStatus.LastSyncAddedCount = addedCount;
                _lastSyncStatus.LastSyncUpdatedCount = updatedCount;
                _lastSyncStatus.LastSyncErrorCount = errorCount;
                _lastSyncStatus.SyncEnabled = true;
                await SaveSyncStats();

                // Cập nhật thời gian đồng bộ trong cài đặt server
                await _serverSettingsService.UpdateLastSyncTimeAsync();

                return (true, $"Đồng bộ hoàn tất. Thêm mới: {addedCount}, Cập nhật: {updatedCount}, Lỗi: {errorCount}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ tất cả profiles từ server");
                _lastSyncStatus.FailedSyncCount++;
                await SaveSyncStats();
                return (false, $"Lỗi: {ex.Message}");
            }
        }

        public async Task<(bool success, string message, int added, int updated, int errors)> ProcessFullSyncAsync(List<SteamCmdProfile> profiles, string clientIp)
        {
            try
            {
                if (profiles == null || profiles.Count == 0)
                {
                    return (false, "Không có profiles để xử lý", 0, 0, 0);
                }

                int added = 0;
                int updated = 0;
                int errors = 0;
                var existingProfiles = await _profileService.GetAllProfiles();

                foreach (var profile in profiles)
                {
                    try
                    {
                        if (profile == null) continue;

                        var existingProfile = existingProfiles.FirstOrDefault(p =>
                            p.Name == profile.Name &&
                            p.AppID == profile.AppID &&
                            p.InstallDirectory == profile.InstallDirectory);

                        if (existingProfile != null)
                        {
                            // Cập nhật thông tin hiện có
                            profile.Id = existingProfile.Id;
                            profile.Status = existingProfile.Status;
                            profile.Pid = existingProfile.Pid;
                            profile.StartTime = existingProfile.StartTime;
                            profile.StopTime = existingProfile.StopTime;

                            await _profileService.UpdateProfile(profile);
                            updated++;
                            _logger.LogInformation("Đã cập nhật profile '{ProfileName}' từ IP {ClientIp}", profile.Name, clientIp);
                        }
                        else
                        {
                            // Thêm mới profile
                            await _profileService.AddProfileAsync(profile);
                            added++;
                            _logger.LogInformation("Đã thêm profile mới '{ProfileName}' từ IP {ClientIp}", profile.Name, clientIp);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý profile '{ProfileName}' từ IP {ClientIp}", profile?.Name, clientIp);
                        errors++;
                    }
                }

                // Cập nhật thống kê
                await LogSyncActivity(clientIp, "full", added, updated, errors);

                string message = $"Đã xử lý {profiles.Count} profile. Thêm mới: {added}, Cập nhật: {updated}, Lỗi: {errors}";
                return (true, message, added, updated, errors);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý full sync từ IP {ClientIp}", clientIp);
                return (false, $"Lỗi: {ex.Message}", 0, 0, 0);
            }
        }

        public SyncStatusResponse GetSyncStatus()
        {
            _lastSyncStatus.CurrentTime = DateTime.Now;
            return _lastSyncStatus;
        }

        private async Task LogSyncActivity(string clientIp, string syncType, int added = 0, int updated = 0, int errors = 0)
        {
            try
            {
                string logDir = Path.Combine(Directory.GetCurrentDirectory(), "Data", "Logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string logFilePath = Path.Combine(logDir, $"silentsync_{DateTime.Now:yyyyMMdd}.log");
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {clientIp} - {syncType} - Added: {added}, Updated: {updated}, Errors: {errors}{Environment.NewLine}";

                await File.AppendAllTextAsync(logFilePath, logEntry);

                // Cập nhật thống kê đồng bộ
                _lastSyncStatus.LastSyncTime = DateTime.Now;
                _lastSyncStatus.TotalSyncCount++;
                _lastSyncStatus.SuccessSyncCount += (errors == 0) ? 1 : 0;
                _lastSyncStatus.FailedSyncCount += (errors > 0) ? 1 : 0;
                _lastSyncStatus.LastSyncAddedCount = added;
                _lastSyncStatus.LastSyncUpdatedCount = updated;
                _lastSyncStatus.LastSyncErrorCount = errors;
                _lastSyncStatus.SyncEnabled = true;

                await SaveSyncStats();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi ghi log sync");
            }
        }

        private SyncStatusResponse LoadSyncStatus()
        {
            try
            {
                if (File.Exists(_syncStatsFilePath))
                {
                    string json = File.ReadAllText(_syncStatsFilePath);
                    return JsonSerializer.Deserialize<SyncStatusResponse>(json)
                        ?? CreateDefaultSyncStatus();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc thống kê sync");
            }

            return CreateDefaultSyncStatus();
        }

        private async Task SaveSyncStats()
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_lastSyncStatus, options);
                await File.WriteAllTextAsync(_syncStatsFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu thống kê sync");
            }
        }

        private SyncStatusResponse CreateDefaultSyncStatus()
        {
            return new SyncStatusResponse
            {
                LastSyncTime = DateTime.MinValue,
                TotalSyncCount = 0,
                SuccessSyncCount = 0,
                FailedSyncCount = 0,
                LastSyncAddedCount = 0,
                LastSyncUpdatedCount = 0,
                LastSyncErrorCount = 0,
                SyncEnabled = true,
                CurrentTime = DateTime.Now
            };
        }
    }
}