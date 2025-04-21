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

        public ProfileService(ILogger<ProfileService> logger)
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

            _profilesPath = Path.Combine(dataDir, "profiles.json");

#if DEBUG
            Console.WriteLine($"Current dir : {currentDir} \nProfile file path : {_profilesPath}"); //pass
#endif
            if (!File.Exists(_profilesPath))
            {
                _logger.LogError("File profiles not exist in :", _profilesPath);
            }
        }

        public async Task<List<SteamCmdProfile>> GetAllProfiles()
        {
            if (!File.Exists(_profilesPath))
            {
                _logger.LogInformation("File profiles.json không tồn tại tại {0}. Trả về danh sách rỗng.", _profilesPath);
                return new List<SteamCmdProfile>();
            }

            try
            {
                string json;
                json = await File.ReadAllTextAsync(_profilesPath).ConfigureAwait(false);
                var profiles = JsonSerializer.Deserialize<List<SteamCmdProfile>>(json) ?? new List<SteamCmdProfile>();
                _logger.LogInformation("Đã đọc {0} profiles từ {1}", profiles.Count, _profilesPath);
                return profiles;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc file profiles.json tại {0}", _profilesPath);
                throw new Exception($"Không thể đọc file profiles.json: {ex.Message}", ex);
            }
        }

        public async Task<SteamCmdProfile> GetProfileById(int id)
        {
            try
            {
                var profiles = await GetAllProfiles();
                var profile = profiles.FirstOrDefault(p => p.Id == id);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {0}", id);
                }
                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy profile với ID {0}", id);
                throw;
            }
        }

        public async Task SaveProfiles(List<SteamCmdProfile> profiles)
        {
            int retryCount = 0;
            int maxRetries = 5;
            int retryDelayMs = 200;

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

                    // Sử dụng FileMode.Create để tạo mới file mỗi lần ghi
                    using (var fileStream = new FileStream(_profilesPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(fileStream))
                    {
                        await writer.WriteAsync(updatedJson);
                    }

                    _logger.LogInformation("Đã lưu {0} profiles vào {1}", profiles.Count, _profilesPath);
                    return;
                }
                catch (IOException ex) when (ex.Message.Contains("being used") || ex.Message.Contains("access") || ex.HResult == -2147024864)
                {
                    retryCount++;
                    _logger.LogWarning("Lần thử {0}/{1}: Không thể truy cập file profiles.json, đang chờ {2}ms",
                        retryCount, maxRetries, retryDelayMs);

                    if (retryCount < maxRetries)
                    {
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
            try
            {
                _logger.LogInformation("Bắt đầu xóa profile với ID {0}", id);
                var profiles = await GetAllProfiles();
                var profile = profiles.FirstOrDefault(p => p.Id == id);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {0} để xóa", id);
                    return false;
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
    }
}