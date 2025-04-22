using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using System.Text.Json;
using BCrypt.Net;

namespace SteamCmdWebAPI.Services
{
    public class UserService
    {
        private readonly ILogger<UserService> _logger;
        private readonly string _usersFilePath;
        private List<User> _users = new List<User>();
        private readonly object _fileLock = new object();

        public UserService(ILogger<UserService> logger)
        {
            _logger = logger;
            var currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(currentDir, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
            }
            _usersFilePath = Path.Combine(dataDir, "users.json");
            LoadUsers();
        }

        private void LoadUsers()
        {
            try
            {
                if (File.Exists(_usersFilePath))
                {
                    string json = File.ReadAllText(_usersFilePath);
                    _users = JsonSerializer.Deserialize<List<User>>(json) ?? new List<User>();
                    _logger.LogInformation("Đã tải {0} người dùng từ {1}", _users.Count, _usersFilePath);
                }
                else
                {
                    _users = new List<User>();
                    _logger.LogInformation("Không tìm thấy file users.json, tạo danh sách trống");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải danh sách người dùng từ {0}", _usersFilePath);
                _users = new List<User>();
            }
        }

        private async Task SaveUsersAsync()
        {
            try
            {
                lock (_fileLock)
                {
                    string json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_usersFilePath, json);
                }
                _logger.LogInformation("Đã lưu {0} người dùng vào {1}", _users.Count, _usersFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu danh sách người dùng vào {0}", _usersFilePath);
                throw;
            }
        }

        public async Task<User> RegisterAsync(string username, string password)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                throw new ArgumentException("Tên đăng nhập và mật khẩu không được để trống");
            }

            if (_users.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Tên đăng nhập đã tồn tại");
            }

            string passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

            var user = new User
            {
                Id = _users.Count > 0 ? _users.Max(u => u.Id) + 1 : 1,
                Username = username,
                PasswordHash = passwordHash,
                CreatedAt = DateTime.Now,
                LastLogin = DateTime.MinValue,
                IsAdmin = _users.Count == 0 // Người dùng đầu tiên là admin
            };

            _users.Add(user);
            await SaveUsersAsync();

            _logger.LogInformation("Đã đăng ký người dùng mới: {0}", username);
            return user;
        }

        public async Task<User> AuthenticateAsync(string username, string password)
        {
            var user = _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                _logger.LogWarning("Đăng nhập thất bại: Không tìm thấy người dùng {0}", username);
                return null;
            }

            bool verified = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);

            if (!verified)
            {
                _logger.LogWarning("Đăng nhập thất bại: Mật khẩu không chính xác cho người dùng {0}", username);
                return null;
            }

            user.LastLogin = DateTime.Now;
            await SaveUsersAsync();

            _logger.LogInformation("Đăng nhập thành công: {0}", username);
            return user;
        }

        public async Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
        {
            var user = _users.FirstOrDefault(u => u.Id == userId);

            if (user == null)
            {
                _logger.LogWarning("Đổi mật khẩu thất bại: Không tìm thấy người dùng với ID {0}", userId);
                return false;
            }

            if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            {
                _logger.LogWarning("Đổi mật khẩu thất bại: Mật khẩu hiện tại không chính xác cho người dùng {0}", user.Username);
                return false;
            }

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            await SaveUsersAsync();

            _logger.LogInformation("Đổi mật khẩu thành công cho người dùng {0}", user.Username);
            return true;
        }

        public bool AnyUsers()
        {
            return _users.Any();
        }

        public User GetUserById(int id)
        {
            return _users.FirstOrDefault(u => u.Id == id);
        }

        public User GetUserByUsername(string username)
        {
            return _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        }
    }
}