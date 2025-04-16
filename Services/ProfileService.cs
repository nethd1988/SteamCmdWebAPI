using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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
                var profiles = JsonConvert.DeserializeObject<List<SteamCmdProfile>>(json) ?? new List<SteamCmdProfile>();
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
            try
            {
                // Kiểm tra quyền truy cập file trước khi ghi
                var directory = Path.GetDirectoryName(_profilesPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.LogInformation("Đã tạo thư mục {0}", directory);
                }

                // Kiểm tra quyền ghi file
                if (File.Exists(_profilesPath))
                {
                    using (var stream = new FileStream(_profilesPath, FileMode.Open, FileAccess.ReadWrite))
                    {
                        if (!stream.CanWrite)
                        {
                            throw new IOException("Không có quyền ghi file profiles.json");
                        }
                    }
                }

                string updatedJson = JsonConvert.SerializeObject(profiles, Formatting.Indented);
                lock (_fileLock)
                {
                    File.WriteAllText(_profilesPath, updatedJson); // Sử dụng phương thức đồng bộ
                }
                _logger.LogInformation("Đã lưu {0} profiles vào {1}", profiles.Count, _profilesPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu profiles vào {0}", _profilesPath);
                throw new Exception($"Không thể lưu file profiles.json: {ex.Message}", ex);
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
    }
}