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

                _logger.LogInformation("GetAccountByAppIdAsync: Đang kiểm tra {Count} tài khoản cho AppID {AppId}", accounts.Count, appId);

                // Tìm tài khoản có chứa AppID được chỉ định
                foreach (var account in accounts)
                {
                    if (string.IsNullOrEmpty(account.AppIds))
                        continue;

                    // Tách AppIds và làm sạch
                    var appIdsList = account.AppIds
                        .Split(',')
                        .Select(id => id.Trim())
                        .Where(id => !string.IsNullOrEmpty(id))
                        .ToList();

                    _logger.LogDebug("GetAccountByAppIdAsync: Tài khoản {Username} có các AppID: {AppIds}",
                        account.Username, string.Join(", ", appIdsList));

                    if (appIdsList.Contains(appId))
                    {
                        _logger.LogInformation("GetAccountByAppIdAsync: Tìm thấy tài khoản {Username} cho AppID {AppId}",
                            account.Username, appId);

                        // Mật khẩu đã mã hóa không cần giải mã ở đây vì sẽ được xử lý trong ProfileService
                        return account;
                    }
                }

                _logger.LogWarning("GetAccountByAppIdAsync: Không tìm thấy tài khoản nào cho AppID {AppId}", appId);
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
                // Mã hóa mật khẩu trước khi lưu
                var accountsToSave = accounts.Select(a => new SteamAccount 
                {
                    Id = a.Id,
                    ProfileName = a.ProfileName,
                    Username = a.Username,
                    Password = _encryptionService.Encrypt(a.Password),
                    AppIds = a.AppIds,
                    GameNames = a.GameNames,
                    CreatedAt = a.CreatedAt,
                    UpdatedAt = a.UpdatedAt
                }).ToList();
                
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(accountsToSave, options);
                File.WriteAllText(_accountsFilePath, json);
            }
        }
        
        public async Task<SteamAccount> AddAccountAsync(SteamAccount account)
        {
            var accounts = await GetAllAccountsAsync();
            
            account.Id = accounts.Count > 0 ? accounts.Max(a => a.Id) + 1 : 1;
            account.CreatedAt = DateTime.Now;
            account.UpdatedAt = DateTime.Now;
            
            accounts.Add(account);
            SaveAccounts(accounts);
            
            return account;
        }
        
        public async Task UpdateAccountAsync(SteamAccount account)
        {
            var accounts = await GetAllAccountsAsync();
            int index = accounts.FindIndex(a => a.Id == account.Id);
            
            if (index == -1)
            {
                throw new Exception($"Không tìm thấy tài khoản với ID {account.Id}");
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
            }
        }
    }
}