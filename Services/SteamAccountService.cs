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
using System.Text.Json.Serialization;

namespace SteamCmdWebAPI.Services
{
    public class SteamAccountService
    {
        private readonly ILogger<SteamAccountService> _logger;
        private readonly string _accountsFilePath;
        private readonly EncryptionService _encryptionService;
        private readonly SteamAppInfoService _steamAppInfoService;
        private readonly object _fileLock = new object();
        
        public SteamAccountService(
            ILogger<SteamAccountService> logger, 
            EncryptionService encryptionService,
            SteamAppInfoService steamAppInfoService)
        {
            _logger = logger;
            _encryptionService = encryptionService;
            _steamAppInfoService = steamAppInfoService;
            
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
                // Tối ưu hóa: Chỉ mã hóa thông tin tài khoản cần thiết
                var accountsToSave = new List<SteamAccount>();
                
                // Thiết lập tùy chọn JSON với WriteIndented để dễ đọc và IncludeFields để bảo đảm tất cả các trường được lưu
                var options = new JsonSerializerOptions { 
                    WriteIndented = true,  // Để dễ đọc khi debug
                    DefaultIgnoreCondition = JsonIgnoreCondition.Never,  // Không bỏ qua bất kỳ trường nào
                    IgnoreReadOnlyProperties = false,  // Bao gồm cả thuộc tính chỉ đọc
                    IncludeFields = true  // Bao gồm tất cả các trường
                }; 
                
                // Ghi log trước khi lưu
                _logger.LogInformation("Đang lưu {Count} tài khoản", accounts.Count);

                // Tạo một danh sách mới để lưu, tránh thay đổi danh sách gốc
                foreach (var account in accounts)
                {
                    _logger.LogDebug("Lưu tài khoản ID {Id} - AutoScanEnabled: {AutoScanEnabled}", 
                        account.Id, account.AutoScanEnabled);
                    
                    // Thêm log để kiểm tra trạng thái AutoScanEnabled trước khi lưu
                    _logger.LogInformation("SaveAccounts: Tài khoản {ProfileName} (ID: {Id}) có AutoScanEnabled = {AutoScanEnabled}", 
                        account.ProfileName, account.Id, account.AutoScanEnabled);
                    
                    // Chỉ xử lý username và password khi cần
                    string usernameToSave = account.Username;
                    string passwordToSave = account.Password;

                    // Không cần kiểm tra mã hóa thủ công nếu đã biết chắc trạng thái
                    var accountToSave = new SteamAccount
                    {
                        Id = account.Id,
                        ProfileName = account.ProfileName,
                        Username = usernameToSave,
                        Password = passwordToSave,
                        AppIds = account.AppIds,
                        GameNames = account.GameNames,
                        CreatedAt = account.CreatedAt,
                        UpdatedAt = account.UpdatedAt,
                        // Đảm bảo các trường bổ sung được sao chép đúng
                        AutoScanEnabled = account.AutoScanEnabled,
                        LastScanTime = account.LastScanTime,
                        NextScanTime = account.NextScanTime,
                        ScanIntervalHours = account.ScanIntervalHours
                    };
                    
                    // Thêm log để kiểm tra giá trị sau khi sao chép
                    _logger.LogInformation("SaveAccounts: Tài khoản sao chép {ProfileName} (ID: {Id}) có AutoScanEnabled = {AutoScanEnabled}", 
                        accountToSave.ProfileName, accountToSave.Id, accountToSave.AutoScanEnabled);
                    
                    accountsToSave.Add(accountToSave);
                }

                try 
                {
                    var json = JsonSerializer.Serialize(accountsToSave, options);
                    
                    // Ghi log trước khi ghi file
                    _logger.LogDebug("SaveAccounts: JSON để ghi vào file: {Json}", json);
                    
                    File.WriteAllText(_accountsFilePath, json);
                    _logger.LogInformation("Đã lưu danh sách tài khoản thành công");
                    
                    // Kiểm tra ngay sau khi lưu
                    string savedJson = File.ReadAllText(_accountsFilePath);
                    _logger.LogDebug("SaveAccounts: Xác minh JSON đã lưu (độ dài: {Length})", savedJson.Length);
                    
                    // Phân tích lại để kiểm tra
                    try {
                        var verifiedAccounts = JsonSerializer.Deserialize<List<SteamAccount>>(savedJson, options);
                        _logger.LogInformation("SaveAccounts: Đã xác minh JSON - Đọc lại được {Count} tài khoản",
                            verifiedAccounts?.Count ?? 0);
                            
                        if (verifiedAccounts != null) {
                            foreach (var acc in verifiedAccounts.Where(a => a.AutoScanEnabled)) {
                                _logger.LogDebug("SaveAccounts: Tài khoản đã lưu {Id} có AutoScanEnabled = {Value}", 
                                    acc.Id, acc.AutoScanEnabled);
                            }
                        }
                    }
                    catch (Exception ex) {
                        _logger.LogWarning(ex, "SaveAccounts: Không thể xác minh JSON đã lưu: {Message}", ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi lưu danh sách tài khoản Steam: {0}", ex.Message);
                    throw;
                }
            }
        }

        public async Task<SteamAccount> AddAccountAsync(SteamAccount account)
        {
            // Giải mã username và password nếu bị mã hóa
            if (!string.IsNullOrEmpty(account.Username))
            {
                try { account.Username = _encryptionService.Decrypt(account.Username); } catch { }
            }
            if (!string.IsNullOrEmpty(account.Password))
            {
                try { account.Password = _encryptionService.Decrypt(account.Password); } catch { }
            }

            var accounts = await GetAllAccountsAsync();

            // Nếu có AppId, tự động lấy thông tin tên game
            if (!string.IsNullOrEmpty(account.AppIds))
            {
                try
                {
                    _logger.LogInformation("Tự động lấy thông tin game từ AppID: {0}", account.AppIds);
                    var appIds = account.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(appIds);
                    
                    // Nếu có kết quả, cập nhật lại AppIds và GameNames
                    if (gameInfos.Count > 0)
                    {
                        var validAppIds = new List<string>();
                        var gameNames = new List<string>();
                        
                        foreach (var (appId, gameName) in gameInfos)
                        {
                            if (!string.IsNullOrEmpty(appId))
                            {
                                validAppIds.Add(appId);
                                
                                if (!string.IsNullOrEmpty(gameName))
                                {
                                    gameNames.Add(gameName);
                                    _logger.LogInformation("Đã lấy được thông tin game: AppID {0} -> {1}", appId, gameName);
                                }
                            }
                        }
                        
                        account.AppIds = string.Join(",", validAppIds);
                        if (gameNames.Count > 0)
                        {
                            account.GameNames = string.Join(",", gameNames);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tự động lấy thông tin game từ AppID: {0}", account.AppIds);
                }
            }

            // Trước khi tìm kiếm tài khoản đã tồn tại, cần mã hóa tên người dùng và mật khẩu
            // để so sánh với các tài khoản đã được mã hóa trong danh sách
            string originalUsername = account.Username; // Lưu lại để dùng khi so sánh
            string originalPassword = account.Password; // Lưu lại để dùng khi so sánh

            // Mã hóa tài khoản trước khi so sánh
            try {
                if (!string.IsNullOrEmpty(account.Username)) {
                    account.Username = _encryptionService.Encrypt(account.Username);
                }
                if (!string.IsNullOrEmpty(account.Password)) {
                    account.Password = _encryptionService.Encrypt(account.Password);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex, "Lỗi khi mã hóa thông tin tài khoản: {0}", ex.Message);
            }

            // Kiểm tra tài khoản đã tồn tại
            var existing = accounts.FirstOrDefault(a => {
                try {
                    // Giải mã username từ database để so sánh với username đầu vào
                    string dbUsername = _encryptionService.Decrypt(a.Username);
                    return dbUsername.Equals(originalUsername, StringComparison.OrdinalIgnoreCase);
                }
                catch {
                    // Nếu không giải mã được, thử so sánh trực tiếp (trường hợp cũ)
                    return a.Username.Equals(account.Username, StringComparison.OrdinalIgnoreCase);
                }
            });

            if (existing != null)
            {
                // Hợp nhất AppIds
                var appIds = (existing.AppIds ?? "").Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                if (!string.IsNullOrEmpty(account.AppIds))
                {
                    var newAppIds = account.AppIds.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x));
                    foreach (var newAppId in newAppIds)
                    {
                        if (!appIds.Contains(newAppId))
                            appIds.Add(newAppId);
                    }
                }
                existing.AppIds = string.Join(",", appIds.Distinct());

                // Hợp nhất GameNames
                var gameNames = (existing.GameNames ?? "").Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                if (!string.IsNullOrEmpty(account.GameNames))
                {
                    var newGameNames = account.GameNames.Split(',').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x));
                    foreach (var newGameName in newGameNames)
                    {
                        if (!gameNames.Contains(newGameName))
                            gameNames.Add(newGameName);
                    }
                }
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
                accounts.Add(account);
                SaveAccounts(accounts);
                return account;
            }
        }

        public async Task<(bool success, string message, DateTime? nextScanTime)> ToggleAutoScanAsync(int accountId, bool enabled)
        {
            try
            {
                var accounts = await GetAllAccountsAsync();
                var account = accounts.FirstOrDefault(a => a.Id == accountId);
                
                if (account == null)
                {
                    _logger.LogWarning("ToggleAutoScanAsync: Không tìm thấy tài khoản ID {AccountId}", accountId);
                    return (false, $"Không tìm thấy tài khoản ID {accountId}", null);
                }
                
                _logger.LogInformation("ToggleAutoScanAsync: Tài khoản {Username} (ID: {Id}), đang chuyển từ {OldState} -> {NewState}", 
                    account.ProfileName, accountId, account.AutoScanEnabled, enabled);
                
                // Cập nhật trạng thái
                var previousState = account.AutoScanEnabled;
                account.AutoScanEnabled = enabled;
                
                _logger.LogDebug("ToggleAutoScanAsync: Đã thay đổi trạng thái từ {Old} -> {New}", previousState, account.AutoScanEnabled);
                
                // Cập nhật thời gian
                if (enabled)
                {
                    // Lấy thời gian quét hiện tại hoặc mặc định là 6 giờ
                    int intervalHours = account.ScanIntervalHours > 0 ? account.ScanIntervalHours : 6;
                    account.ScanIntervalHours = intervalHours; // Đảm bảo có interval
                    
                    // Thiết lập thời gian quét tiếp theo
                    account.NextScanTime = DateTime.Now.AddHours(intervalHours);
                    
                    _logger.LogInformation("ToggleAutoScanAsync: Đã bật quét tự động, quét tiếp theo vào {NextScanTime}", 
                        account.NextScanTime);
                }
                else
                {
                    _logger.LogInformation("ToggleAutoScanAsync: Đã tắt quét tự động");
                }
                
                // Lưu thay đổi
                account.UpdatedAt = DateTime.Now;
                
                // Kiểm tra trạng thái trước khi lưu
                _logger.LogDebug("ToggleAutoScanAsync: Trước khi lưu - Tài khoản {Id} có AutoScanEnabled = {AutoScanEnabled}", 
                    account.Id, account.AutoScanEnabled);
                
                SaveAccounts(accounts);
                
                // Kiểm tra lại sau khi lưu bằng cách đọc lại
                var refreshedAccounts = await GetAllAccountsAsync();
                var refreshedAccount = refreshedAccounts.FirstOrDefault(a => a.Id == accountId);
                
                if (refreshedAccount != null)
                {
                    _logger.LogDebug("ToggleAutoScanAsync: Sau khi lưu - Tài khoản {Id} có AutoScanEnabled = {AutoScanEnabled}", 
                        refreshedAccount.Id, refreshedAccount.AutoScanEnabled);
                        
                    if (refreshedAccount.AutoScanEnabled != enabled)
                    {
                        _logger.LogWarning("ToggleAutoScanAsync: Giá trị AutoScanEnabled không khớp sau khi lưu! Expected: {Expected}, Actual: {Actual}",
                            enabled, refreshedAccount.AutoScanEnabled);
                    }
                }
                
                _logger.LogInformation("ToggleAutoScanAsync: Đã lưu trạng thái mới, AutoScanEnabled: {AutoScanEnabled}", account.AutoScanEnabled);
                
                // Trả về kết quả thành công và thời gian quét tiếp theo
                return (true, enabled ? "Đã bật quét tự động" : "Đã tắt quét tự động", account.NextScanTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ToggleAutoScanAsync: Lỗi khi cập nhật trạng thái quét tự động cho tài khoản ID {AccountId}", accountId);
                return (false, "Lỗi khi cập nhật trạng thái quét tự động", null);
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
            
            // Ghi log trạng thái tự động quét
            _logger.LogInformation("UpdateAccountAsync: AutoScanEnabled trước khi cập nhật: {Old}, sau khi cập nhật: {New}", 
                accounts[index].AutoScanEnabled, account.AutoScanEnabled);
                
            // Ghi log thông tin GameNames
            _logger.LogInformation("UpdateAccountAsync: GameNames hiện tại: {GameNames}", account.GameNames);

            // Chỉ tự động lấy thông tin tên game nếu AppId mới được cung cấp và GameNames trống
            if (!string.IsNullOrEmpty(account.AppIds) && string.IsNullOrEmpty(account.GameNames))
            {
                _logger.LogInformation("UpdateAccountAsync: AppIds có dữ liệu nhưng GameNames trống, tự động lấy thông tin game");
                try
                {
                    // Chỉ xử lý các AppId chưa có trong GameNames
                    var existingAccountAppIds = (accounts[index].AppIds ?? "").Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    var existingGameNames = (accounts[index].GameNames ?? "").Split(',').Select(name => name.Trim()).Where(name => !string.IsNullOrEmpty(name)).ToList();
                    
                    var currentAppIds = account.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    
                    // Tìm các AppId mới cần lấy thông tin
                    var newAppIds = currentAppIds.Except(existingAccountAppIds).ToList();
                    
                    if (newAppIds.Count > 0)
                    {
                        _logger.LogInformation("Tự động lấy thông tin cho {0} AppID mới: {1}", newAppIds.Count, string.Join(", ", newAppIds));
                        var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(newAppIds);
                        
                        if (gameInfos.Count > 0)
                        {
                            var gameNames = new List<string>(existingGameNames);
                            
                            foreach (var (appId, gameName) in gameInfos)
                            {
                                if (!string.IsNullOrEmpty(gameName) && !gameNames.Contains(gameName))
                                {
                                    gameNames.Add(gameName);
                                    _logger.LogInformation("Đã lấy được thông tin game: AppID {0} -> {1}", appId, gameName);
                                }
                            }
                            
                            account.GameNames = string.Join(",", gameNames.Distinct());
                            _logger.LogInformation("UpdateAccountAsync: GameNames sau khi lấy thông tin: {GameNames}", account.GameNames);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Không có AppID mới cần lấy thông tin game");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tự động lấy thông tin game từ AppID: {0}", account.AppIds);
                }
            }
            else
            {
                _logger.LogInformation("UpdateAccountAsync: Giữ nguyên GameNames hiện tại vì đã được cung cấp hoặc AppIds trống");
            }

            // Mã hóa thông tin tài khoản trước khi cập nhật
            try
            {
                // Mã hóa username nếu có
                if (!string.IsNullOrEmpty(account.Username))
                {
                    // Thử giải mã để kiểm tra xem đã mã hóa chưa
                    try
                    {
                        string decrypted = _encryptionService.Decrypt(account.Username);
                        // Nếu giải mã thành công, mã hóa lại để đảm bảo dùng mã hóa mới nhất
                        account.Username = _encryptionService.Encrypt(decrypted);
                    }
                    catch
                    {
                        // Nếu không giải mã được, giả sử chuỗi chưa mã hóa
                        account.Username = _encryptionService.Encrypt(account.Username);
                    }
                }

                // Mã hóa password nếu có
                if (!string.IsNullOrEmpty(account.Password))
                {
                    // Thử giải mã để kiểm tra xem đã mã hóa chưa
                    try
                    {
                        string decrypted = _encryptionService.Decrypt(account.Password);
                        // Nếu giải mã thành công, mã hóa lại để đảm bảo dùng mã hóa mới nhất
                        account.Password = _encryptionService.Encrypt(decrypted);
                    }
                    catch
                    {
                        // Nếu không giải mã được, giả sử chuỗi chưa mã hóa
                        account.Password = _encryptionService.Encrypt(account.Password);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi mã hóa thông tin tài khoản để cập nhật: {0}", ex.Message);
            }

            // Cập nhật thông tin quét tự động
            if (account.AutoScanEnabled)
            {
                // Đảm bảo có ScanIntervalHours
                if (account.ScanIntervalHours <= 0)
                {
                    account.ScanIntervalHours = 6; // Mặc định 6 giờ
                }
                
                // Cập nhật thời gian quét tiếp theo nếu chưa có
                if (!account.NextScanTime.HasValue)
                {
                    account.NextScanTime = DateTime.Now.AddHours(account.ScanIntervalHours);
                }
                
                _logger.LogInformation("UpdateAccountAsync: Đã bật quét tự động, interval: {Interval}h, quét tiếp theo: {NextTime}",
                    account.ScanIntervalHours, account.NextScanTime);
            }
            else
            {
                _logger.LogInformation("UpdateAccountAsync: Đã tắt quét tự động");
            }

            account.UpdatedAt = DateTime.Now;
            accounts[index] = account;

            SaveAccounts(accounts);
            
            // Ghi log sau khi đã lưu
            _logger.LogInformation("UpdateAccountAsync: Đã cập nhật tài khoản {ProfileName} (ID: {Id}), AutoScanEnabled: {AutoScan}", 
                account.ProfileName, account.Id, account.AutoScanEnabled);
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
                    UpdatedAt = account.UpdatedAt,
                    // Copy all auto-scan related properties
                    AutoScanEnabled = account.AutoScanEnabled,
                    LastScanTime = account.LastScanTime,
                    NextScanTime = account.NextScanTime,
                    ScanIntervalHours = account.ScanIntervalHours
                };
                
                _logger.LogDebug("DecryptAndReencryptIfNeeded: Tài khoản {ProfileName} (ID: {Id}) có AutoScanEnabled = {AutoScanEnabled}", 
                    processedAccount.ProfileName, processedAccount.Id, processedAccount.AutoScanEnabled);
                
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

        // Phương thức mới để tự động cập nhật thông tin game cho tất cả tài khoản
        public async Task UpdateAllGamesInfoAsync()
        {
            try
            {
                _logger.LogInformation("Bắt đầu cập nhật thông tin game cho tất cả tài khoản");
                var accounts = await GetAllAccountsAsync();
                int totalAccounts = accounts.Count;
                int processedAccounts = 0;
                int updatedAccounts = 0;

                foreach (var account in accounts)
                {
                    try
                    {
                        processedAccounts++;
                        _logger.LogInformation("Đang xử lý tài khoản {Current}/{Total}: {Username}",
                            processedAccounts, totalAccounts, account.ProfileName);

                        if (string.IsNullOrEmpty(account.AppIds))
                        {
                            _logger.LogInformation("Tài khoản {Username} không có AppId, bỏ qua", account.ProfileName);
                            continue;
                        }

                        // Lấy danh sách AppIds
                        var appIds = account.AppIds.Split(',')
                            .Select(id => id.Trim())
                            .Where(id => !string.IsNullOrEmpty(id))
                            .ToList();

                        // Lấy danh sách GameNames hiện tại
                        var existingGameNames = !string.IsNullOrEmpty(account.GameNames)
                            ? account.GameNames.Split(',')
                                .Select(name => name.Trim())
                                .Where(name => !string.IsNullOrEmpty(name))
                                .ToList()
                            : new List<string>();

                        // Nếu số lượng game names bằng số lượng appids, có thể bỏ qua
                        if (existingGameNames.Count == appIds.Count)
                        {
                            _logger.LogInformation("Tài khoản {Username} đã có đủ thông tin game ({Count}), bỏ qua",
                                account.ProfileName, existingGameNames.Count);
                            continue;
                        }

                        // Lấy thông tin game từ Steam
                        var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(appIds);
                        var gameNames = new List<string>();

                        foreach (var (appId, gameName) in gameInfos)
                        {
                            if (!string.IsNullOrEmpty(gameName))
                            {
                                gameNames.Add(gameName);
                                _logger.LogInformation("Đã lấy được thông tin game: AppID {0} -> {1}", appId, gameName);
                            }
                        }

                        // Cập nhật account nếu có thông tin game mới
                        if (gameNames.Count > existingGameNames.Count)
                        {
                            account.GameNames = string.Join(",", gameNames);
                            await UpdateAccountAsync(account);
                            updatedAccounts++;
                            _logger.LogInformation("Đã cập nhật thông tin game cho tài khoản {Username}", account.ProfileName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi cập nhật thông tin game cho tài khoản {Username}", account.ProfileName);
                    }
                }

                _logger.LogInformation("Hoàn thành cập nhật thông tin game: Đã xử lý {Processed}/{Total} tài khoản, cập nhật {Updated} tài khoản",
                    processedAccounts, totalAccounts, updatedAccounts);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật thông tin game cho tất cả tài khoản");
            }
        }

        // Thêm phương thức để mã hóa lại tất cả tài khoản hiện có
        public async Task ReencryptAllAccountsAsync()
        {
            try
            {
                _logger.LogInformation("Bắt đầu mã hóa lại tất cả tài khoản...");
                
                // Đọc tất cả tài khoản từ tệp
                var accounts = await GetAllAccountsAsync();
                int encryptedCount = 0;

                // Kiểm tra và mã hóa từng tài khoản
                foreach (var account in accounts)
                {
                    bool modified = false;

                    // Kiểm tra và mã hóa username
                    if (!string.IsNullOrEmpty(account.Username))
                    {
                        try
                        {
                            // Thử giải mã để kiểm tra xem đã mã hóa chưa
                            string decrypted = _encryptionService.Decrypt(account.Username);
                            _logger.LogDebug("Tài khoản {ProfileName}: Username đã được mã hóa, tái mã hóa", account.ProfileName);
                            // Mã hóa lại từ chuỗi đã giải mã
                            account.Username = _encryptionService.Encrypt(decrypted);
                            modified = true;
                        }
                        catch
                        {
                            _logger.LogInformation("Tài khoản {ProfileName}: Username chưa mã hóa, đang mã hóa", account.ProfileName);
                            // Nếu không giải mã được, giả sử chuỗi chưa mã hóa
                            account.Username = _encryptionService.Encrypt(account.Username);
                            modified = true;
                        }
                    }

                    // Kiểm tra và mã hóa password
                    if (!string.IsNullOrEmpty(account.Password))
                    {
                        try
                        {
                            // Thử giải mã để kiểm tra xem đã mã hóa chưa
                            string decrypted = _encryptionService.Decrypt(account.Password);
                            _logger.LogDebug("Tài khoản {ProfileName}: Password đã được mã hóa, tái mã hóa", account.ProfileName);
                            // Mã hóa lại từ chuỗi đã giải mã
                            account.Password = _encryptionService.Encrypt(decrypted);
                            modified = true;
                        }
                        catch
                        {
                            _logger.LogInformation("Tài khoản {ProfileName}: Password chưa mã hóa, đang mã hóa", account.ProfileName);
                            // Nếu không giải mã được, giả sử chuỗi chưa mã hóa
                            account.Password = _encryptionService.Encrypt(account.Password);
                            modified = true;
                        }
                    }

                    if (modified)
                    {
                        encryptedCount++;
                    }
                }

                // Lưu lại tất cả tài khoản đã được mã hóa
                if (encryptedCount > 0)
                {
                    SaveAccounts(accounts);
                    _logger.LogInformation("Đã mã hóa lại {Count} tài khoản", encryptedCount);
                }
                else
                {
                    _logger.LogInformation("Không có tài khoản nào cần mã hóa lại");
                }

                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi mã hóa lại tài khoản: {Message}", ex.Message);
                throw;
            }
        }

        public async Task<List<SteamAccount>> GetAllAvailableAccounts()
        {
            try
            {
                var accounts = await GetAllAccountsAsync();
                var availableAccounts = new List<SteamAccount>();

                foreach (var account in accounts)
                {
                    try
                    {
                        // Tạo bản sao để không ảnh hưởng đến object gốc
                        var processedAccount = new SteamAccount
                        {
                            Id = account.Id,
                            ProfileName = account.ProfileName,
                            Username = account.Username,
                            Password = account.Password,
                            AppIds = account.AppIds,
                            GameNames = account.GameNames,
                            CreatedAt = account.CreatedAt,
                            UpdatedAt = account.UpdatedAt,
                            AutoScanEnabled = account.AutoScanEnabled,
                            LastScanTime = account.LastScanTime,
                            NextScanTime = account.NextScanTime,
                            ScanIntervalHours = account.ScanIntervalHours
                        };

                        // Giải mã username
                        if (!string.IsNullOrEmpty(processedAccount.Username))
                        {
                            try
                            {
                                processedAccount.Username = _encryptionService.Decrypt(processedAccount.Username);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Không thể giải mã username: {Error}", ex.Message);
                                // Bỏ qua tài khoản này nếu không giải mã được username
                                continue;
                            }
                        }
                        else
                        {
                            // Bỏ qua tài khoản không có username
                            continue;
                        }

                        // Giải mã password
                        if (!string.IsNullOrEmpty(processedAccount.Password))
                        {
                            try
                            {
                                processedAccount.Password = _encryptionService.Decrypt(processedAccount.Password);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning("Không thể giải mã password: {Error}", ex.Message);
                                // Bỏ qua tài khoản này nếu không giải mã được password
                                continue;
                            }
                        }
                        else
                        {
                            // Bỏ qua tài khoản không có password
                            continue;
                        }

                        // Chỉ thêm các tài khoản có đủ thông tin đăng nhập hợp lệ
                        if (!string.IsNullOrEmpty(processedAccount.Username) && !string.IsNullOrEmpty(processedAccount.Password))
                        {
                            availableAccounts.Add(processedAccount);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi xử lý tài khoản {Id}: {Error}", account.Id, ex.Message);
                    }
                }

                _logger.LogInformation("Tìm thấy {Count} tài khoản khả dụng", availableAccounts.Count);
                return availableAccounts;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách tài khoản khả dụng: {Error}", ex.Message);
                return new List<SteamAccount>();
            }
        }
    }
}