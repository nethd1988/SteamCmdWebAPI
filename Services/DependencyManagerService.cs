using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Models.Dependencies;

namespace SteamCmdWebAPI.Services
{
    public class DependencyManagerService
    {
        private readonly ILogger<DependencyManagerService> _logger;
        private readonly string _dependenciesFilePath;
        private readonly ProfileService _profileService;
        private readonly SteamApiService _steamApiService;
        private readonly object _fileLock = new object();

        public DependencyManagerService(
            ILogger<DependencyManagerService> logger,
            ProfileService profileService,
            SteamApiService steamApiService)
        {
            _logger = logger;
            _profileService = profileService;
            _steamApiService = steamApiService;

            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(currentDir, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
            }

            _dependenciesFilePath = Path.Combine(dataDir, "dependencies.json");

            if (!File.Exists(_dependenciesFilePath))
            {
                SaveDependencies(new List<ProfileDependency>());
                _logger.LogInformation("Tạo file dependencies.json");
            }
        }

        public async Task<List<ProfileDependency>> GetAllDependenciesAsync()
        {
            try
            {
                if (!File.Exists(_dependenciesFilePath))
                {
                    return new List<ProfileDependency>();
                }

                string json = await File.ReadAllTextAsync(_dependenciesFilePath);
                var dependencies = JsonSerializer.Deserialize<List<ProfileDependency>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return dependencies ?? new List<ProfileDependency>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc file dependencies: {Message}", ex.Message);
                return new List<ProfileDependency>();
            }
        }

        public async Task<ProfileDependency> GetDependencyByProfileIdAsync(int profileId)
        {
            var dependencies = await GetAllDependenciesAsync();
            return dependencies.FirstOrDefault(d => d.ProfileId == profileId);
        }

        public void SaveDependencies(List<ProfileDependency> dependencies)
        {
            try
            {
                lock (_fileLock)
                {
                    var options = new JsonSerializerOptions { WriteIndented = true };
                    string json = JsonSerializer.Serialize(dependencies, options);
                    File.WriteAllText(_dependenciesFilePath, json);
                }
                _logger.LogInformation("Đã lưu {0} dependencies vào file", dependencies.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu file dependencies: {Message}", ex.Message);
                throw;
            }
        }

        public async Task UpdateDependenciesAsync(int profileId, string mainAppId, List<string> dependentAppIds)
        {
            try
            {
                var dependencies = await GetAllDependenciesAsync();
                var dependency = dependencies.FirstOrDefault(d => d.ProfileId == profileId);

                if (dependency == null)
                {
                    dependency = new ProfileDependency
                    {
                        Id = dependencies.Count > 0 ? dependencies.Max(d => d.Id) + 1 : 1,
                        ProfileId = profileId,
                        MainAppId = mainAppId,
                        CreatedAt = DateTime.Now
                    };
                    dependencies.Add(dependency);
                }

                // Cập nhật App ID chính
                dependency.MainAppId = mainAppId;
                dependency.UpdatedAt = DateTime.Now;

                // Lọc các AppId phụ thuộc, loại bỏ AppId chính
                var filteredDependentAppIds = dependentAppIds
                    .Where(id => id != mainAppId)
                    .Distinct()
                    .ToList();

                // Cập nhật danh sách phụ thuộc
                var existingAppIds = dependency.DependentApps.Select(a => a.AppId).ToList();
                
                // Loại bỏ các AppID không còn trong danh sách mới
                dependency.DependentApps.RemoveAll(app => !filteredDependentAppIds.Contains(app.AppId));
                
                // Thêm các AppID mới
                foreach (var appId in filteredDependentAppIds)
                {
                    if (!existingAppIds.Contains(appId))
                    {
                        var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                        var app = new DependentApp
                        {
                            AppId = appId,
                            Name = appInfo?.Name ?? $"AppID {appId}",
                            NeedsUpdate = false,
                            LastUpdateCheck = DateTime.Now
                        };
                        dependency.DependentApps.Add(app);
                    }
                }

                SaveDependencies(dependencies);
                _logger.LogInformation("Đã cập nhật dependencies cho profile ID {0} với {1} app phụ thuộc", 
                    profileId, dependency.DependentApps.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật dependencies cho profile ID {0}: {1}", profileId, ex.Message);
                throw;
            }
        }

        public async Task<List<string>> ScanDependenciesFromManifest(string steamappsDir, string mainAppId)
        {
            var result = new List<string>();
            try
            {
                if (!Directory.Exists(steamappsDir))
                {
                    _logger.LogWarning("Thư mục steamapps không tồn tại: {0}", steamappsDir);
                    return result;
                }

                var manifests = Directory.GetFiles(steamappsDir, "appmanifest_*.acf");
                var regex = new Regex(@"appmanifest_(\d+)\.acf");

                foreach (var manifest in manifests)
                {
                    var match = regex.Match(Path.GetFileName(manifest));
                    if (match.Success)
                    {
                        string appId = match.Groups[1].Value;
                        if (appId != mainAppId && !result.Contains(appId))
                        {
                            result.Add(appId);
                        }
                    }
                }

                _logger.LogInformation("Đã quét được {0} app phụ thuộc từ thư mục {1}", result.Count, steamappsDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi quét dependencies từ manifest: {0}", ex.Message);
            }

            return result;
        }

        public async Task MarkAppForUpdateAsync(string appId)
        {
            try
            {
                var dependencies = await GetAllDependenciesAsync();
                bool updated = false;

                foreach (var dependency in dependencies)
                {
                    // Kiểm tra nếu appId là app chính
                    if (dependency.MainAppId == appId)
                    {
                        _logger.LogInformation("AppID {0} là app chính của profile ID {1}", appId, dependency.ProfileId);
                        continue; // Không đánh dấu app chính ở đây
                    }

                    // Tìm trong các app phụ thuộc
                    var dependentApp = dependency.DependentApps.FirstOrDefault(a => a.AppId == appId);
                    if (dependentApp != null)
                    {
                        dependentApp.NeedsUpdate = true;
                        dependentApp.LastUpdateCheck = DateTime.Now;
                        updated = true;
                        _logger.LogInformation("Đánh dấu AppID {0} (thuộc profile ID {1}) cần cập nhật", 
                            appId, dependency.ProfileId);
                    }
                }

                if (updated)
                {
                    SaveDependencies(dependencies);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đánh dấu AppID {0} cần cập nhật: {1}", appId, ex.Message);
            }
        }

        public async Task ResetUpdateFlagsAsync(int profileId, string appId = null)
        {
            try
            {
                var dependencies = await GetAllDependenciesAsync();
                var dependency = dependencies.FirstOrDefault(d => d.ProfileId == profileId);

                if (dependency == null)
                {
                    return;
                }

                if (string.IsNullOrEmpty(appId))
                {
                    // Reset tất cả app phụ thuộc của profile
                    foreach (var app in dependency.DependentApps)
                    {
                        app.NeedsUpdate = false;
                        app.LastUpdateCheck = DateTime.Now;
                    }
                }
                else
                {
                    // Reset chỉ app được chỉ định
                    var app = dependency.DependentApps.FirstOrDefault(a => a.AppId == appId);
                    if (app != null)
                    {
                        app.NeedsUpdate = false;
                        app.LastUpdateCheck = DateTime.Now;
                    }
                }

                SaveDependencies(dependencies);
                _logger.LogInformation("Đã reset cờ cập nhật cho {0}", 
                    string.IsNullOrEmpty(appId) ? $"tất cả app của profile ID {profileId}" : $"AppID {appId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi reset cờ cập nhật: {0}", ex.Message);
            }
        }

        public async Task<List<DependentApp>> GetAppsNeedingUpdateAsync(int profileId)
        {
            var dependency = await GetDependencyByProfileIdAsync(profileId);
            return dependency?.DependentApps.Where(a => a.NeedsUpdate).ToList() ?? new List<DependentApp>();
        }
    }
}