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
                    return new List<SteamAccount>();
                }
                
                string json = await File.ReadAllTextAsync(_accountsFilePath);
                var accounts = JsonSerializer.Deserialize<List<SteamAccount>>(json) ?? new List<SteamAccount>();
                
                // Giải mã mật khẩu
                foreach (var account in accounts)
                {
                    try
                    {
                        account.Password = _encryptionService.Decrypt(account.Password);
                    }
                    catch
                    {
                        // Nếu không giải mã được, giữ nguyên giá trị
                    }
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
            if (string.IsNullOrEmpty(appId))
            {
                _logger.LogWarning("GetAccountByAppIdAsync: AppID trống");
                return null;
            }
            
            var accounts = await GetAllAccountsAsync();
            
            // Tìm tài khoản có chứa AppID
            return accounts.FirstOrDefault(acc => 
                !string.IsNullOrEmpty(acc.AppIds) && 
                acc.AppIds.Split(',').Select(a => a.Trim()).Contains(appId));
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