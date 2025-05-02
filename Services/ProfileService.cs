// Thêm các phương thức từ ProfileMigrationService vào ProfileService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class ProfileService
    {
        private readonly string _profilesPath;
        private readonly ILogger<ProfileService> _logger;
        private readonly object _fileLock = new object(); // Khóa để xử lý đồng thời
        private readonly string _backupFolder;
        private readonly SettingsService _settingsService;
        private readonly EncryptionService _encryptionService;
        private readonly LicenseService _licenseService;
        private readonly string _settingsPath;
        private readonly object _lock = new object();
        private List<SteamCmdProfile> _profiles;

        public ProfileService(
            ILogger<ProfileService> logger,
            SettingsService settingsService,
            EncryptionService encryptionService,
            LicenseService licenseService)
        {
            _logger = logger;
            _settingsService = settingsService;
            _encryptionService = encryptionService;
            _licenseService = licenseService;
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            // Lưu file profiles.json trong thư mục data
            string dataDir = Path.Combine(currentDir, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
            }

            _profilesPath = Path.Combine(dataDir, "profiles.json");
            _backupFolder = Path.Combine(dataDir, "Backup");

            if (!Directory.Exists(_backupFolder))
            {
                Directory.CreateDirectory(_backupFolder);
            }

            if (!File.Exists(_profilesPath))
            {
                _logger.LogInformation("File profiles chưa tồn tại, sẽ được tạo khi cần.");
            }

            _settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
            _profiles = new List<SteamCmdProfile>();
            EnsureSettingsFileExists();
        }

        private void EnsureSettingsFileExists()
        {
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    _logger.LogInformation("File settings.json không tồn tại. Đang tạo file mới...");
                    var defaultSettings = new { Profiles = new List<SteamCmdProfile>() };
                    string jsonString = JsonSerializer.Serialize(defaultSettings, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_settingsPath, jsonString);
                    _logger.LogInformation("Đã tạo file settings.json mới.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo file settings.json: {Message}", ex.Message);
                throw;
            }
        }

        public async Task<List<SteamCmdProfile>> GetAllProfiles()
        {
            int retryCount = 0;
            const int maxRetries = 3;
            const int retryDelayMs = 500;

            while (retryCount < maxRetries)
            {
                try
                {
                    if (!File.Exists(_profilesPath))
                    {
                        _logger.LogInformation("File profiles.json không tồn tại tại {0}. Trả về danh sách rỗng.", _profilesPath);
                        return new List<SteamCmdProfile>();
                    }

                    using (var fileStream = new FileStream(_profilesPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fileStream))
                    {
                        string json = await reader.ReadToEndAsync().ConfigureAwait(false);
                        var profiles = JsonSerializer.Deserialize<List<SteamCmdProfile>>(json) ?? new List<SteamCmdProfile>();
                        _logger.LogInformation("Đã đọc {0} profiles từ {1}", profiles.Count, _profilesPath);
                        return profiles;
                    }
                }
                catch (IOException ex) when (ex.Message.Contains("being used by another process"))
                {
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        _logger.LogWarning("Lần thử {0}/{1}: File profiles.json đang bị khóa, đợi {2}ms...", 
                            retryCount, maxRetries, retryDelayMs);
                        await Task.Delay(retryDelayMs);
                    }
                    else
                    {
                        _logger.LogError("Không thể đọc file profiles.json sau {0} lần thử: {1}", maxRetries, ex.Message);
                        throw new Exception($"Không thể đọc file profiles.json: {ex.Message}", ex);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi đọc file profiles.json tại {0}", _profilesPath);
                    throw new Exception($"Không thể đọc file profiles.json: {ex.Message}", ex);
                }
            }

            throw new Exception("Không thể đọc file profiles.json sau nhiều lần thử");
        }

        public async Task<SteamCmdProfile> GetProfileById(int id)
        {
            try
            {
                var profiles = await GetAllProfiles();
                return profiles.FirstOrDefault(p => p.Id == id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin profile theo ID");
                return null;
            }
        }

        /// <summary>
        /// Lấy tất cả các profile chứa AppID cụ thể
        /// </summary>
        public async Task<List<SteamCmdProfile>> GetProfilesByAppId(string appId)
        {
            try
            {
                if (string.IsNullOrEmpty(appId))
                {
                    return new List<SteamCmdProfile>();
                }

                var profiles = await GetAllProfiles();
                return profiles.Where(p => 
                    !string.IsNullOrEmpty(p.AppID) && 
                    p.AppID.Split(',').Select(a => a.Trim()).Contains(appId)
                ).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile theo AppID: {AppId}", appId);
                return new List<SteamCmdProfile>();
            }
        }

        public async Task SaveProfiles(List<SteamCmdProfile> profiles)
        {
            int retryCount = 0;
            const int maxRetries = 5;
            const int initialRetryDelayMs = 200;
            int retryDelayMs = initialRetryDelayMs;

            while (retryCount < maxRetries)
            {
                try
                {
                    var directory = Path.GetDirectoryName(_profilesPath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                        _logger.LogInformation("Đã tạo thư mục {0}", directory);
                    }

                    string updatedJson = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });

                    // Sử dụng FileShare.Read để cho phép đọc trong khi đang ghi
                    using (var fileStream = new FileStream(_profilesPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var writer = new StreamWriter(fileStream))
                    {
                        await writer.WriteAsync(updatedJson);
                        await writer.FlushAsync();
                    }

                    _logger.LogInformation("Đã lưu {0} profiles vào {1}", profiles.Count, _profilesPath);
                    return;
                }
                catch (IOException ex) when (ex.Message.Contains("being used by another process"))
                {
                    retryCount++;
                    if (retryCount < maxRetries)
                    {
                        _logger.LogWarning("Lần thử {0}/{1}: File profiles.json đang bị khóa, đợi {2}ms...", 
                            retryCount, maxRetries, retryDelayMs);
                        await Task.Delay(retryDelayMs);
                        retryDelayMs *= 2; // Tăng thời gian chờ theo cấp số nhân
                    }
                    else
                    {
                        _logger.LogError("Không thể lưu profiles.json sau {0} lần thử: {1}", maxRetries, ex.Message);
                        throw new Exception($"Không thể lưu file profiles.json sau nhiều lần thử: {ex.Message}", ex);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi lưu profiles vào {0}", _profilesPath);
                    throw new Exception($"Không thể lưu file profiles.json: {ex.Message}", ex);
                }
            }
        }

        public async Task AddProfileAsync(SteamCmdProfile profile)
        {
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                throw new Exception("License không hợp lệ, không thể thực hiện thao tác");
            }

            try
            {
                _logger.LogInformation("Bắt đầu thêm profile: {Name}", profile.Name);
                var profiles = await GetAllProfiles();
                int newId = profiles.Count > 0 ? profiles.Max(p => p.Id) + 1 : 1;
                profile.Id = newId;
                profiles.Add(profile);
                await SaveProfiles(profiles);
                _logger.LogInformation("Đã thêm profile thành công: {Name} với ID {Id}", profile.Name, profile.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm profile mới: {Name}", profile.Name);
                throw;
            }
        }

        public async Task<bool> DeleteProfile(int id)
        {
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                return false;
            }

            try
            {
                _logger.LogInformation("Bắt đầu xóa profile với ID {0}", id);
                var profiles = await GetAllProfiles();
                var profile = profiles.FirstOrDefault(p => p.Id == id);
                
                // Nếu profile không tồn tại, coi như đã xóa thành công
                if (profile == null)
                {
                    _logger.LogInformation("Profile với ID {0} không tồn tại, coi như đã xóa thành công", id);
                    return true;
                }

                profiles.Remove(profile);
                await SaveProfiles(profiles);
                _logger.LogInformation("Đã xóa profile với ID {0}", id);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa profile với ID {0}", id);
                throw;
            }
        }

        public async Task UpdateProfile(SteamCmdProfile updatedProfile)
        {
            if (!await _licenseService.CheckLicenseBeforeOperationAsync())
            {
                _logger.LogError("License không hợp lệ, không thể thực hiện thao tác");
                throw new Exception("License không hợp lệ, không thể thực hiện thao tác");
            }

            try
            {
                _logger.LogInformation("Bắt đầu cập nhật profile với ID {0}", updatedProfile.Id);
                var profiles = await GetAllProfiles();
                int index = profiles.FindIndex(p => p.Id == updatedProfile.Id);
                if (index == -1)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {0} để cập nhật", updatedProfile.Id);
                    throw new Exception($"Không tìm thấy profile với ID {updatedProfile.Id} để cập nhật.");
                }

                profiles[index] = updatedProfile;
                await SaveProfiles(profiles);
                _logger.LogInformation("Đã cập nhật profile thành công với ID {0}", updatedProfile.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật profile với ID {0}", updatedProfile.Id);
                throw;
            }
        }

        public async Task<AutoRunSettings> LoadAutoRunSettings()
        {
            try
            {
                string settingsFilePath = Path.Combine(Path.GetDirectoryName(_profilesPath), "settings.json");

                if (!File.Exists(settingsFilePath))
                {
                    _logger.LogInformation("File settings.json không tồn tại. Trả về cài đặt mặc định.");
                    return new AutoRunSettings
                    {
                        AutoRunEnabled = false,
                        AutoRunIntervalHours = 12,
                        AutoRunInterval = "daily"
                    };
                }

                string json = await File.ReadAllTextAsync(settingsFilePath);
                var settings = JsonSerializer.Deserialize<AutoRunSettings>(json);

                if (settings == null)
                {
                    return new AutoRunSettings
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

                // Giới hạn khoảng thời gian hợp lệ
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;

                return settings;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc cài đặt auto run");
                return new AutoRunSettings
                {
                    AutoRunEnabled = false,
                    AutoRunIntervalHours = 12,
                    AutoRunInterval = "daily"
                };
            }
        }

        public async Task SaveAutoRunSettings(AutoRunSettings settings)
        {
            try
            {
                string settingsFilePath = Path.Combine(Path.GetDirectoryName(_profilesPath), "settings.json");

                var directory = Path.GetDirectoryName(settingsFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogInformation("Đã tạo thư mục {0}", directory);
                }

                // Kiểm tra và điều chỉnh giá trị
                if (settings.AutoRunIntervalHours < 1) settings.AutoRunIntervalHours = 1;
                if (settings.AutoRunIntervalHours > 48) settings.AutoRunIntervalHours = 48;

                // Cập nhật chuỗi AutoRunInterval cho tương thích ngược
                settings.AutoRunInterval = ConvertIntervalHoursToString(settings.AutoRunIntervalHours);

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(settingsFilePath, json);

                _logger.LogInformation("Đã lưu cài đặt auto run: Enabled={0}, IntervalHours={1}",
                    settings.AutoRunEnabled, settings.AutoRunIntervalHours);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu cài đặt auto run");
                throw;
            }
        }

        private string ConvertIntervalHoursToString(int hours)
        {
            if (hours <= 24) return "daily";
            if (hours <= 168) return "weekly";
            return "monthly";
        }

        // Phương thức từ ProfileMigrationService
        public List<BackupFileInfo> GetBackupFiles()
        {
            try
            {
                var result = new List<BackupFileInfo>();
                var directory = new DirectoryInfo(_backupFolder);

                if (!directory.Exists)
                {
                    return result;
                }

                var files = directory.GetFiles("*.json").OrderByDescending(f => f.LastWriteTime);

                foreach (var file in files)
                {
                    result.Add(new BackupFileInfo
                    {
                        FileName = file.Name,
                        CreationTime = file.CreationTime,
                        LastWriteTime = file.LastWriteTime,
                        SizeBytes = file.Length,
                        SizeMB = Math.Round(file.Length / 1024.0 / 1024.0, 2)
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách file backup");
                return new List<BackupFileInfo>();
            }
        }

        public async Task<string> BackupProfiles(List<SteamCmdProfile> profiles)
        {
            try
            {
                if (profiles == null || profiles.Count == 0)
                {
                    return "Không có profile nào để backup";
                }

                if (!Directory.Exists(_backupFolder))
                {
                    Directory.CreateDirectory(_backupFolder);
                }

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"backup_{timestamp}.json";
                string filePath = Path.Combine(_backupFolder, fileName);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(profiles, options);
                await File.WriteAllTextAsync(filePath, json);

                return $"Đã backup {profiles.Count} profile vào file {fileName}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi backup profiles");
                throw;
            }
        }

        public async Task<List<SteamCmdProfile>> LoadProfilesFromBackup(string fileName)
        {
            try
            {
                string filePath = Path.Combine(_backupFolder, fileName);

                if (!File.Exists(filePath))
                {
                    return new List<SteamCmdProfile>();
                }

                string json = await File.ReadAllTextAsync(filePath);
                return JsonSerializer.Deserialize<List<SteamCmdProfile>>(json) ?? new List<SteamCmdProfile>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc profiles từ backup");
                throw;
            }
        }

        public async Task<(int Added, int Skipped)> MigrateProfilesToAppProfiles(List<SteamCmdProfile> profiles, bool skipDuplicateCheck = false)
        {
            try
            {
                if (profiles == null || profiles.Count == 0)
                {
                    return (0, 0);
                }

                int added = 0;
                int skipped = 0;
                var existingProfiles = await GetAllProfiles();

                foreach (var profile in profiles)
                {
                    // Bỏ qua profile null
                    if (profile == null) continue;

                    // Kiểm tra trùng lặp nếu không bỏ qua
                    if (!skipDuplicateCheck)
                    {
                        bool isDuplicate = existingProfiles.Any(p =>
                            p.Name == profile.Name &&
                            p.AppID == profile.AppID &&
                            p.InstallDirectory == profile.InstallDirectory);

                        if (isDuplicate)
                        {
                            skipped++;
                            continue;
                        }
                    }

                    // Đặt ID mới để tránh xung đột với ID hiện có
                    int newId = existingProfiles.Count > 0 ? existingProfiles.Max(p => p.Id) + 1 : 1;
                    profile.Id = newId;

                    // Đặt trạng thái mặc định
                    if (string.IsNullOrEmpty(profile.Status))
                    {
                        profile.Status = "Stopped";
                    }

                    // Thêm profile mới
                    await AddProfileAsync(profile);
                    existingProfiles.Add(profile); // Cập nhật danh sách local để tính ID mới chính xác
                    added++;
                }

                return (added, skipped);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi di chuyển profiles");
                throw;
            }
        }
    }

    // Class để lưu trữ thông tin file backup
    public class BackupFileInfo
    {
        public string FileName { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime LastWriteTime { get; set; }
        public long SizeBytes { get; set; }
        public double SizeMB { get; set; }
    }
}