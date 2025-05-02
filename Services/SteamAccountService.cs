using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using System.Net.Http;
using System.Net.Http.Json;

namespace SteamCmdWebAPI.Services
{
    public class SteamAccountService
    {
        private readonly ILogger<SteamAccountService> _logger;
        private readonly string _accountsFilePath;
        private readonly EncryptionService _encryptionService;
        private readonly object _fileLock = new object();
        
        public SteamAccountService(ILogger<SteamAccountService> logger, EncryptionService encryptionService)
        {
            _logger = logger;
            _encryptionService = encryptionService;
            
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
            }
            
            _accountsFilePath = Path.Combine(dataDir, "steam_accounts.json");
            
            if (!File.Exists(_accountsFilePath))
            {
                SaveAccounts(new List<SteamAccount>());
                logger.LogInformation("Đã tạo file steam_accounts.json");
            }
        }

        public async Task<List<SteamAccount>> GetAllAccountsAsync()
        {
            try
            {
                if (!File.Exists(_accountsFilePath))
                {
                    _logger.LogWarning("File steam_accounts.json không tồn tại tại {0}. Trả về danh sách rỗng.", _accountsFilePath);
                    return new List<SteamAccount>();
                }

                string json = await File.ReadAllTextAsync(_accountsFilePath);
                _logger.LogDebug("GetAllAccountsAsync: Đọc file json dài {Length} ký tự", json.Length);

                var accounts = JsonSerializer.Deserialize<List<SteamAccount>>(json) ?? new List<SteamAccount>();

                _logger.LogInformation("GetAllAccountsAsync: Đọc được {Count} tài khoản", accounts.Count);

                // In log chi tiết để debug
                foreach (var account in accounts)
                {
                    _logger.LogDebug("GetAllAccountsAsync: Tài khoản {Username} có AppIds: {AppIds}",
                        account.Username, account.AppIds);
                }

                return accounts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc danh sách tài khoản Steam từ {0}", _accountsFilePath);
                return new List<SteamAccount>();
            }
        }

        public async Task<SteamAccount> GetAccountByIdAsync(int id)
        {
            var accounts = await GetAllAccountsAsync();
            return accounts.FirstOrDefault(a => a.Id == id);
        }

        public async Task<SteamAccount> GetAccountByAppIdAsync(string appId)
        {
            try
            {
                if (string.IsNullOrEmpty(appId))
                {
                    _logger.LogWarning("GetAccountByAppIdAsync: AppID trống");
                    return null;
                }

                var accounts = await GetAllAccountsAsync();

                foreach (var account in accounts)
                {
                    if (string.IsNullOrEmpty(account.AppIds))
                        continue;

                    var appIdsList = account.AppIds
                        .Split(',')
                        .Select(id => id.Trim())
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToList();

                    if (appIdsList.Contains(appId))
                    {
                        _logger.LogInformation("Tìm thấy tài khoản {Username} cho AppID {AppId}", account.Username, appId);

                        // Giải mã tên tài khoản nếu cần thiết
                        try
                        {
                            // Kiểm tra xem tên tài khoản có cần giải mã không
                            if (!string.IsNullOrEmpty(account.Username) && account.Username.Length > 20)
                            {
                                // Tên tài khoản có vẻ đã mã hóa, cố gắng giải mã
                                string decryptedUsername = _encryptionService.Decrypt(account.Username);
                                if (!string.IsNullOrEmpty(decryptedUsername))
                                {
                                    account.Username = decryptedUsername;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã tên tài khoản cho tài khoản ID {Id}", account.Id);
                            // Giữ nguyên username gốc nếu giải mã thất bại
                        }

                        // Không giải mã mật khẩu - giữ nguyên mật khẩu đã mã hóa để sử dụng
                        return account;
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tìm tài khoản cho AppID {AppId}", appId);
                return null;
            }
        }

        public void SaveAccounts(List<SteamAccount> accounts)
        {
            lock (_fileLock)
            {
                var accountsToSave = new List<SteamAccount>();

                foreach (var account in accounts)
                {
                    string passwordToSave = account.Password;

                    // Kiểm tra xem mật khẩu có cần mã hóa không
                    // Mật khẩu khi mã hóa thường dài và có các ký tự đặc biệt
                    if (!string.IsNullOrEmpty(passwordToSave) &&
                        (passwordToSave.Length < 30 || !passwordToSave.Contains("/")))
                    {
                        // Mật khẩu chưa được mã hóa
                        passwordToSave = _encryptionService.Encrypt(passwordToSave);
                        _logger.LogDebug("Đã mã hóa mật khẩu cho tài khoản {Username}", account.Username);
                    }

                    accountsToSave.Add(new SteamAccount
                    {
                        Id = account.Id,
                        ProfileName = account.ProfileName,
                        Username = account.Username,
                        Password = passwordToSave,
                        AppIds = account.AppIds,
                        GameNames = account.GameNames,
                        CreatedAt = account.CreatedAt,
                        UpdatedAt = account.UpdatedAt
                    });
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(accountsToSave, options);
                File.WriteAllText(_accountsFilePath, json);
            }
        }

        public async Task<SteamAccount> AddAccountAsync(SteamAccount account)
        {
            var accounts = await GetAllAccountsAsync();

            // Kiểm tra tài khoản đã tồn tại (theo Username)
            var existing = accounts.FirstOrDefault(a => a.Username == account.Username);
            if (existing != null)
            {
                // Hợp nhất AppIds
                var appIds = (existing.AppIds ?? "").Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                if (!string.IsNullOrEmpty(account.AppIds) && !appIds.Contains(account.AppIds))
                    appIds.Add(account.AppIds);
                existing.AppIds = string.Join(",", appIds.Distinct());

                // Hợp nhất GameNames
                var gameNames = (existing.GameNames ?? "").Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                if (!string.IsNullOrEmpty(account.GameNames) && !gameNames.Contains(account.GameNames))
                    gameNames.Add(account.GameNames);
                existing.GameNames = string.Join(",", gameNames.Distinct());

                existing.UpdatedAt = DateTime.Now;
                SaveAccounts(accounts);
                return existing;
            }
            else
            {
            account.Id = accounts.Count > 0 ? accounts.Max(a => a.Id) + 1 : 1;
            account.CreatedAt = DateTime.Now;
            account.UpdatedAt = DateTime.Now;
            if (!string.IsNullOrEmpty(account.Password) && account.Password.Length < 20)
            {
                account.Password = _encryptionService.Encrypt(account.Password);
            }
            accounts.Add(account);
            SaveAccounts(accounts);
            return account;
            }
        }

        public async Task UpdateAccountAsync(SteamAccount account)
        {
            var accounts = await GetAllAccountsAsync();
            int index = accounts.FindIndex(a => a.Id == account.Id);

            if (index == -1)
            {
                throw new Exception($"Không tìm thấy tài khoản với ID {account.Id}");
            }

            // Kiểm tra xem mật khẩu có cần mã hóa không
            if (!string.IsNullOrEmpty(account.Password) && account.Password.Length < 20)
            {
                account.Password = _encryptionService.Encrypt(account.Password);
            }

            account.UpdatedAt = DateTime.Now;
            accounts[index] = account;

            SaveAccounts(accounts);
        }

        public async Task DeleteAccountAsync(int id)
        {
            var accounts = await GetAllAccountsAsync();
            var account = accounts.FirstOrDefault(a => a.Id == id);
            
            if (account != null)
            {
                accounts.Remove(account);
                SaveAccounts(accounts);
                _logger.LogInformation("Đã xóa tài khoản {Username} (ID: {Id})", account.Username, id);
            }
            else
            {
                _logger.LogWarning("Không tìm thấy tài khoản với ID {Id} để xóa", id);
                throw new Exception($"Không tìm thấy tài khoản với ID {id}");
            }
        }

        // Hàm đồng bộ tất cả SteamAccount lên server
        public async Task SyncAllAccountsToServerAsync(string serverBaseUrl)
        {
            try
            {
                var accounts = await GetAllAccountsAsync();
                using var httpClient = new HttpClient();
                foreach (var account in accounts)
                {
                    // Giải mã mật khẩu trước khi gửi lên server
                    string decryptedPassword = account.Password;
                    if (!string.IsNullOrEmpty(account.Password) && account.Password.Length > 20)
                    {
                        try
                        {
                            decryptedPassword = _encryptionService.Decrypt(account.Password);
                            _logger.LogDebug("Đã giải mã mật khẩu cho tài khoản {Username}", account.Username);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã mật khẩu cho tài khoản {Username}", account.Username);
                            // Giữ nguyên mật khẩu gốc nếu giải mã thất bại
                        }
                    }
                    
                    // Giải mã tài khoản nếu cần
                    string decryptedUsername = account.Username;
                    if (!string.IsNullOrEmpty(account.Username) && account.Username.Length > 20)
                    {
                        try
                        {
                            decryptedUsername = _encryptionService.Decrypt(account.Username);
                            _logger.LogDebug("Đã giải mã tên tài khoản {Username}", account.Username);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã tên tài khoản {Username}", account.Username);
                            // Giữ nguyên username gốc nếu giải mã thất bại
                        }
                    }

                    // Chuyển đổi sang ClientProfile (chỉ lấy AppId đầu tiên nếu có nhiều)
                    var appId = account.AppIds?.Split(',')[0]?.Trim() ?? string.Empty;
                    var profile = new ClientProfile
                    {
                        Name = account.ProfileName,
                        AppID = appId,
                        SteamUsername = decryptedUsername, // Sử dụng tên tài khoản đã giải mã
                        SteamPassword = decryptedPassword, // Sử dụng mật khẩu đã giải mã
                        InstallDirectory = string.Empty, // Client sẽ chọn sau
                        Arguments = string.Empty,
                        ValidateFiles = false,
                        AutoRun = false,
                        AnonymousLogin = false,
                        Status = "Ready",
                        StartTime = DateTime.Now,
                        StopTime = DateTime.Now,
                        Pid = 0,
                        LastRun = DateTime.Now
                    };
                    var url = $"{serverBaseUrl.TrimEnd('/')}/api/profiles";
                    try
                    {
                        var resp = await httpClient.PostAsJsonAsync(url, profile);
                        if (!resp.IsSuccessStatusCode)
                        {
                            _logger.LogWarning("SyncAllAccountsToServerAsync: Không gửi được profile {Name} lên server. Status: {Status}", profile.Name, resp.StatusCode);
                        }
                        else
                        {
                            _logger.LogInformation("SyncAllAccountsToServerAsync: Đã gửi profile {Name} lên server thành công", profile.Name);
                        }
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "SyncAllAccountsToServerAsync: Lỗi khi gửi profile {Name} lên server", profile.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncAllAccountsToServerAsync: Lỗi khi đồng bộ tài khoản lên server {ServerUrl}", serverBaseUrl);
            }
        }
    }
}