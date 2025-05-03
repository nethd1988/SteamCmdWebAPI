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
                        _logger.LogInformation("Tìm thấy tài khoản cho AppID {AppId}", appId);

                        // Tạo bản sao để không ảnh hưởng đến object gốc trong bộ nhớ cache
                        var processedAccount = new SteamAccount
                        {
                            Id = account.Id,
                            ProfileName = account.ProfileName,
                            Username = account.Username,
                            Password = account.Password,
                            AppIds = account.AppIds,
                            GameNames = account.GameNames,
                            CreatedAt = account.CreatedAt,
                            UpdatedAt = account.UpdatedAt
                        };

                        // Thử giải mã username
                        if (!string.IsNullOrEmpty(processedAccount.Username))
                        {
                            try
                            {
                                string decryptedUsername = _encryptionService.Decrypt(processedAccount.Username);
                                _logger.LogInformation("Đã giải mã username thành công: {0}", decryptedUsername.Substring(0, Math.Min(3, decryptedUsername.Length)) + "***");
                                // Trả về username đã giải mã
                                processedAccount.Username = decryptedUsername;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Không thể giải mã username, có thể đã mã hóa bằng khóa khác: {0}", ex.Message);
                                // Nếu không thể giải mã, chúng ta vẫn trả về bản gốc
                            }
                        }

                        // Thử giải mã mật khẩu
                        if (!string.IsNullOrEmpty(processedAccount.Password))
                        {
                            try
                            {
                                string decryptedPassword = _encryptionService.Decrypt(processedAccount.Password);
                                _logger.LogInformation("Đã giải mã password thành công, độ dài: {0}", decryptedPassword.Length);
                                // Trả về mật khẩu đã giải mã
                                processedAccount.Password = decryptedPassword;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Không thể giải mã password, có thể đã mã hóa bằng khóa khác: {0}", ex.Message);
                                // Nếu không thể giải mã, chúng ta vẫn trả về bản gốc
                            }
                        }

                        return processedAccount;
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
                    string usernameToSave = account.Username;
                    string passwordToSave = account.Password;
                    
                    // Kiểm tra xem username có cần mã hóa không
                    if (!string.IsNullOrEmpty(usernameToSave))
                    {
                        try
                        {
                            // Thử giải mã để kiểm tra xem đã được mã hóa chưa
                            _encryptionService.Decrypt(usernameToSave);
                            // Nếu không có ngoại lệ, chuỗi đã được mã hóa
                            _logger.LogDebug("Username đã được mã hóa, giữ nguyên: {UsernameHint}***", 
                                usernameToSave.Length > 3 ? usernameToSave.Substring(0, 3) : "***");
                        }
                        catch (Exception)
                        {
                            // Nếu có ngoại lệ, chuỗi chưa được mã hóa - mã hóa nó
                            if (usernameToSave.Length < 30)
                            {
                                // Chỉ mã hóa nếu có vẻ như là chuỗi thường (không phải chuỗi đã mã hóa từ server)
                                usernameToSave = _encryptionService.Encrypt(usernameToSave);
                                _logger.LogDebug("Đã mã hóa username cho tài khoản {ProfileName}", account.ProfileName);
                            }
                        }
                    }

                    // Kiểm tra xem mật khẩu có cần mã hóa không
                    if (!string.IsNullOrEmpty(passwordToSave))
                    {
                        try
                        {
                            // Thử giải mã để kiểm tra xem đã được mã hóa chưa
                            _encryptionService.Decrypt(passwordToSave);
                            // Nếu không có ngoại lệ, chuỗi đã được mã hóa
                            _logger.LogDebug("Password đã được mã hóa, giữ nguyên");
                        }
                        catch (Exception)
                        {
                            // Nếu có ngoại lệ, chuỗi chưa được mã hóa - mã hóa nó
                            if (passwordToSave.Length < 30)
                            {
                                // Chỉ mã hóa nếu có vẻ như là chuỗi thường (không phải chuỗi đã mã hóa từ server)
                                passwordToSave = _encryptionService.Encrypt(passwordToSave);
                                _logger.LogDebug("Đã mã hóa password cho tài khoản {ProfileName}", account.ProfileName);
                            }
                        }
                    }

                    accountsToSave.Add(new SteamAccount
                    {
                        Id = account.Id,
                        ProfileName = account.ProfileName,
                        Username = usernameToSave,
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
                // Không mã hóa ở đây, để SaveAccounts lo việc này
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

            // Không mã hóa ở đây, để SaveAccounts lo việc này
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
                    try
                    {
                        // Giải mã tài khoản nếu cần
                        var decryptedAccount = DecryptAndReencryptIfNeeded(account);
                        
                        if (decryptedAccount == null)
                        {
                            _logger.LogWarning("Không thể xử lý giải mã tài khoản {ProfileName}, bỏ qua", account.ProfileName);
                            continue;
                        }
                        
                        // Kiểm tra xem tài khoản đã được giải mã chưa
                        bool usernameDecrypted = decryptedAccount.Username != account.Username;
                        bool passwordDecrypted = decryptedAccount.Password != account.Password;
                        
                        _logger.LogDebug("Tài khoản {ProfileName}: Username {UsernameStatus}, Password {PasswordStatus}",
                            decryptedAccount.ProfileName,
                            usernameDecrypted ? "đã giải mã" : "chưa giải mã",
                            passwordDecrypted ? "đã giải mã" : "chưa giải mã");

                        // Chuyển đổi sang ClientProfile (chỉ lấy AppId đầu tiên nếu có nhiều)
                        var appId = decryptedAccount.AppIds?.Split(',')[0]?.Trim() ?? string.Empty;
                        var profile = new ClientProfile
                        {
                            Name = decryptedAccount.ProfileName,
                            AppID = appId,
                            SteamUsername = decryptedAccount.Username, // Sử dụng tên tài khoản đã giải mã
                            SteamPassword = decryptedAccount.Password, // Sử dụng mật khẩu đã giải mã
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
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý tài khoản {ProfileName} để đồng bộ lên server", account.ProfileName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncAllAccountsToServerAsync: Lỗi khi đồng bộ tài khoản lên server {ServerUrl}", serverBaseUrl);
            }
        }

        // Phương thức mới để giải mã và mã hóa lại nếu cần thiết
        public SteamAccount DecryptAndReencryptIfNeeded(SteamAccount account)
        {
            if (account == null) return null;
            
            try
            {
                // Tạo bản sao để không ảnh hưởng đến account gốc
                var processedAccount = new SteamAccount
                {
                    Id = account.Id,
                    ProfileName = account.ProfileName,
                    Username = account.Username,
                    Password = account.Password,
                    AppIds = account.AppIds,
                    GameNames = account.GameNames,
                    CreatedAt = account.CreatedAt,
                    UpdatedAt = account.UpdatedAt
                };
                
                // Thử giải mã username
                if (!string.IsNullOrEmpty(processedAccount.Username))
                {
                    try
                    {
                        string decryptedUsername = _encryptionService.Decrypt(processedAccount.Username);
                        _logger.LogDebug("Giải mã thành công username: {OriginalLength} -> {DecryptedLength} ký tự", 
                            processedAccount.Username.Length, decryptedUsername.Length);
                        // Thay thế giá trị cũ bằng giá trị đã giải mã
                        processedAccount.Username = decryptedUsername;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Không thể giải mã username, có thể đã mã hóa bằng khóa khác: {Message}", ex.Message);
                        // Giữ nguyên giá trị username nếu không giải mã được
                    }
                }
                
                // Tương tự với password
                if (!string.IsNullOrEmpty(processedAccount.Password))
                {
                    try
                    {
                        string decryptedPassword = _encryptionService.Decrypt(processedAccount.Password);
                        _logger.LogDebug("Giải mã thành công password với độ dài {DecryptedLength} ký tự", decryptedPassword.Length);
                        // Thay thế giá trị cũ bằng giá trị đã giải mã
                        processedAccount.Password = decryptedPassword;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Không thể giải mã password, có thể đã mã hóa bằng khóa khác: {Message}", ex.Message);
                        // Giữ nguyên giá trị password nếu không giải mã được
                    }
                }
                
                return processedAccount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý giải mã tài khoản: {ProfileName}", account.ProfileName);
                return account; // Trả về nguyên bản nếu xảy ra lỗi
            }
        }
    }
}