using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System.Linq;

namespace SteamCmdWebAPI.Pages
{
    public class SteamAccountsModel : PageModel
    {
        private readonly ILogger<SteamAccountsModel> _logger;
        private readonly SteamAccountService _accountService;
        private readonly SteamApiService _steamApiService;
        private readonly EncryptionService _encryptionService;
        private readonly SteamAppInfoService _steamAppInfoService;

        public List<SteamAccount> Accounts { get; set; } = new List<SteamAccount>();

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        [BindProperty]
        public SteamAccount Account { get; set; }

        public SteamAccountsModel(
            ILogger<SteamAccountsModel> logger, 
            SteamAccountService accountService,
            SteamApiService steamApiService,
            EncryptionService encryptionService,
            SteamAppInfoService steamAppInfoService)
        {
            _logger = logger;
            _accountService = accountService;
            _steamApiService = steamApiService;
            _encryptionService = encryptionService;
            _steamAppInfoService = steamAppInfoService;
        }

        public async Task OnGetAsync()
        {
            try
            {
                Accounts = await _accountService.GetAllAccountsAsync();
                
                // Don't decrypt usernames for display - we'll mask them in the view
                // Leave usernames encrypted to protect sensitive information
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error loading account list: {ex.Message}";
                IsSuccess = false;
            }
        }

        public async Task<IActionResult> OnGetGetAccountAsync(int id)
        {
            try
            {
                var account = await _accountService.GetAccountByIdAsync(id);
                if (account == null)
                {
                    return new JsonResult(new { success = false, message = "Account not found" });
                }

                // Tạo một bản sao account để tránh thay đổi dữ liệu từ database
                var accountCopy = new SteamAccount
                {
                    Id = account.Id,
                    ProfileName = account.ProfileName,
                    Username = account.Username,
                    AppIds = account.AppIds,
                    GameNames = account.GameNames,
                    CreatedAt = account.CreatedAt
                };

                // Giải mã username trước khi gửi về UI
                try {
                    if (!string.IsNullOrEmpty(accountCopy.Username)) 
                    {
                        string originalUsername = accountCopy.Username;
                        bool isLikelyEncrypted = originalUsername.Length > 20 && 
                                                originalUsername.Contains("=") && 
                                                !originalUsername.Contains(" ");
                                                
                        if (isLikelyEncrypted)
                        {
                            accountCopy.Username = _encryptionService.Decrypt(originalUsername);
                            _logger.LogDebug("Đã giải mã username từ {OriginalLength} ký tự thành {DecryptedLength} ký tự",
                                originalUsername.Length,
                                accountCopy.Username?.Length ?? 0);
                        }
                        else 
                        {
                            _logger.LogDebug("Username '{UsernameHint}' có vẻ chưa được mã hóa, giữ nguyên giá trị",
                                originalUsername.Length > 3 ? originalUsername.Substring(0, 3) + "***" : "***");
                        }
                    }
                }
                catch (Exception ex) {
                    _logger.LogWarning(ex, "Không thể giải mã username cho UI: {Error}. Trả về giá trị an toàn", ex.Message);
                    
                    // Sử dụng username gốc (được che dấu ở client) hoặc placeholder
                    if (!string.IsNullOrEmpty(accountCopy.Username) && accountCopy.Username.Length > 3)
                    {
                        // Lấy 3 ký tự đầu tiên và thay phần còn lại bằng ***
                        accountCopy.Username = accountCopy.Username.Substring(0, 3) + "***";
                    }
                    else
                    {
                        accountCopy.Username = "<hidden>";
                    }
                }

                // Don't return any password
                accountCopy.Password = null;

                return new JsonResult(new { success = true, account = accountCopy });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting account info {AccountId}", id);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostAddAccountAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Account.ProfileName) || string.IsNullOrWhiteSpace(Account.Username) || string.IsNullOrWhiteSpace(Account.Password))
                {
                    return new JsonResult(new { success = false, message = "Please fill in all required fields" });
                }

                // Get game names from AppIds if available
                if (!string.IsNullOrWhiteSpace(Account.AppIds))
                {
                    var appIds = Account.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(appIds);
                    var gameNames = new List<string>();
                    
                    foreach (var (appId, gameName) in gameInfos)
                    {
                        if (!string.IsNullOrEmpty(gameName))
                        {
                            gameNames.Add(gameName);
                            _logger.LogInformation("Retrieved game info: AppID {0} -> {1}", appId, gameName);
                        }
                        else
                        {
                            // Try using SteamApiService if SteamAppInfoService doesn't have the info
                            try
                            {
                                var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                                gameNames.Add(appInfo?.Name ?? $"AppID {appId}");
                            }
                            catch
                            {
                                gameNames.Add($"AppID {appId}");
                            }
                        }
                    }
                    
                    Account.GameNames = string.Join(", ", gameNames);
                }

                await _accountService.AddAccountAsync(Account);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding account");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostUpdateAccountAsync()
        {
            try
            {
                if (Account.Id <= 0)
                {
                    return new JsonResult(new { success = false, message = "Invalid account ID" });
                }

                var existingAccount = await _accountService.GetAccountByIdAsync(Account.Id);
                if (existingAccount == null)
                {
                    return new JsonResult(new { success = false, message = "Account not found" });
                }

                // Keep old password if no new password
                if (string.IsNullOrWhiteSpace(Account.Password))
                {
                    Account.Password = existingAccount.Password;
                }

                // Keep old username if no new username
                if (string.IsNullOrWhiteSpace(Account.Username))
                {
                    Account.Username = existingAccount.Username;
                }

                // Update game names if AppIds changed
                if (Account.AppIds != existingAccount.AppIds)
                {
                    var appIds = Account.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(appIds);
                    var gameNames = new List<string>();
                    
                    foreach (var (appId, gameName) in gameInfos)
                    {
                        if (!string.IsNullOrEmpty(gameName))
                        {
                            gameNames.Add(gameName);
                            _logger.LogInformation("Retrieved game info: AppID {0} -> {1}", appId, gameName);
                        }
                        else
                        {
                            // Try using SteamApiService if SteamAppInfoService doesn't have the info
                            try
                            {
                                var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                                gameNames.Add(appInfo?.Name ?? $"AppID {appId}");
                            }
                            catch
                            {
                                gameNames.Add($"AppID {appId}");
                            }
                        }
                    }
                    
                    Account.GameNames = string.Join(", ", gameNames);
                }
                else
                {
                    Account.GameNames = existingAccount.GameNames;
                }

                // Update other info
                Account.CreatedAt = existingAccount.CreatedAt;

                await _accountService.UpdateAccountAsync(Account);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating account {AccountId}", Account.Id);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostDeleteAccountAsync(int id)
        {
            try
            {
                if (id <= 0)
                {
                    return new JsonResult(new { success = false, message = "Invalid account ID" });
                }

                var account = await _accountService.GetAccountByIdAsync(id);
                if (account == null)
                {
                    return new JsonResult(new { success = false, message = "Account not found" });
                }

                await _accountService.DeleteAccountAsync(id);
                _logger.LogInformation("Successfully deleted account ID {Id}", id);
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting account {AccountId}", id);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostScanAppIdsAsync(string appIds)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(appIds))
                {
                    return new JsonResult(new { success = false, message = "Please enter AppIDs before scanning" });
                }

                var appIdsList = appIds.Split(',')
                    .Select(id => id.Trim())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToList();

                if (appIdsList.Count == 0)
                {
                    return new JsonResult(new { success = false, message = "No valid AppIDs found" });
                }

                _logger.LogInformation("Scanning info for {Count} AppIDs: {AppIds}", appIdsList.Count, string.Join(", ", appIdsList));

                // Use SteamAppInfoService to get game info
                var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(appIdsList);
                var results = new List<object>();

                foreach (var (appId, gameName) in gameInfos)
                {
                    results.Add(new {
                        appId,
                        gameName = !string.IsNullOrEmpty(gameName) ? gameName : $"AppID {appId}"
                    });
                }

                return new JsonResult(new { 
                    success = true, 
                    results,
                    gameNames = string.Join(", ", results.Select(r => ((dynamic)r).gameName).ToList()),
                    appIds = string.Join(",", results.Select(r => ((dynamic)r).appId).ToList())
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning AppIDs: {AppIds}", appIds);
                return new JsonResult(new { success = false, message = $"Scan error: {ex.Message}" });
            }
        }

        public async Task<IActionResult> OnPostScanAccountGamesAsync(string username, string password, int? accountId = null)
        {
            try
            {
                // Nếu là edit tài khoản (có accountId) và username trống, lấy username hiện tại
                if (accountId.HasValue && accountId.Value > 0 && string.IsNullOrWhiteSpace(username))
                {
                    var existingAccount = await _accountService.GetAccountByIdAsync(accountId.Value);
                    if (existingAccount != null)
                    {
                        _logger.LogInformation("ScanAccountGamesAsync: Username trống, sử dụng username đã lưu cho tài khoản ID {AccountId}", accountId.Value);
                        username = existingAccount.Username;
                    }
                    else
                    {
                        return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản" });
                    }
                }
                
                if (string.IsNullOrWhiteSpace(username))
                {
                    return new JsonResult(new { success = false, message = "Vui lòng nhập tên đăng nhập Steam" });
                }

                // Kiểm tra nhanh xem username có vẻ đã được mã hóa chưa
                bool isUsernameLikelyEncrypted = !string.IsNullOrEmpty(username) && 
                    username.Length > 20 && 
                    username.Contains("=") && 
                    !username.Contains(" ");
                
                _logger.LogDebug("ScanAccountGamesAsync: Username có vẻ đã mã hóa: {Encrypted}, Độ dài: {Length}", 
                    isUsernameLikelyEncrypted, username?.Length ?? 0);
                
                if (isUsernameLikelyEncrypted) {
                    // Nếu username từ form có vẻ đã mã hóa, thử giải mã nó
                    try {
                        string decodedUsername = _encryptionService.Decrypt(username);
                        _logger.LogWarning("ScanAccountGamesAsync: Form đang gửi username đã mã hóa. Đã giải mã thành công.");
                        username = decodedUsername;
                    }
                    catch {
                        _logger.LogWarning("ScanAccountGamesAsync: Username có vẻ đã mã hóa nhưng không thể giải mã. Tiếp tục với giá trị ban đầu.");
                    }
                }

                // Nếu là edit tài khoản (có accountId) và không có mật khẩu, lấy mật khẩu hiện tại
                if (accountId.HasValue && accountId.Value > 0 && string.IsNullOrWhiteSpace(password))
                {
                    var existingAccount = await _accountService.GetAccountByIdAsync(accountId.Value);
                    if (existingAccount != null)
                    {
                        _logger.LogInformation("Sử dụng mật khẩu đã lưu cho tài khoản ID {AccountId}", accountId.Value);
                        
                        // Cần giải mã mật khẩu trước khi sử dụng
                        try {
                            // Không giải mã mật khẩu ngay - sẽ để ScanAccountGames xử lý
                        password = existingAccount.Password;
                            _logger.LogDebug("Đã lấy mật khẩu đã mã hóa từ database, ScanAccountGames sẽ giải mã");
                        }
                        catch (Exception ex) {
                            _logger.LogWarning(ex, "Không thể truy xuất mật khẩu từ database: {0}", ex.Message);
                            return new JsonResult(new { success = false, message = "Không thể truy xuất mật khẩu từ database" });
                        }
                        
                        // Kiểm tra xem tên đăng nhập có thay đổi không
                        if (!isUsernameLikelyEncrypted) // Chỉ kiểm tra nếu username chưa bị mã hóa
                        {
                            // Giải mã username từ database để so sánh với username từ form
                            try {
                                string decryptedDbUsername = _encryptionService.Decrypt(existingAccount.Username);
                                
                                if (username != decryptedDbUsername)
                                {
                                    _logger.LogWarning("Username form khác với giá trị đã lưu trong database sau khi giải mã DB");
                                    
                                    // Log chi tiết để debug
                                    _logger.LogDebug("Username từ form: {FormUsername}, Username từ DB (giải mã): {DbUsername}", 
                                        username.Substring(0, Math.Min(5, username.Length)) + "...",
                                        decryptedDbUsername.Substring(0, Math.Min(5, decryptedDbUsername.Length)) + "...");
                                    
                                    return new JsonResult(new { success = false, message = "Bạn đã thay đổi tên đăng nhập, vui lòng nhập mật khẩu" });
                                }
                                else
                                {
                                    _logger.LogDebug("Username khớp với giá trị đã lưu trong database sau khi giải mã DB");
                                }
                            }
                            catch (Exception ex) {
                                _logger.LogWarning(ex, "Lỗi khi giải mã username từ database: {0}", ex.Message);
                                
                                // Mã hóa username từ form để so sánh
                                try {
                                    string encodedFormUsername = _encryptionService.Encrypt(username);
                                    _logger.LogDebug("Mã hóa username từ form để so sánh: {EncodedUsername}", 
                                        encodedFormUsername.Substring(0, Math.Min(20, encodedFormUsername.Length)) + "...");
                                    
                                    // So sánh với username đã mã hóa từ database
                                    if (!string.Equals(encodedFormUsername, existingAccount.Username))
                        {
                                        _logger.LogWarning("Username form khác với giá trị đã lưu trong database sau khi mã hóa form");
                            return new JsonResult(new { success = false, message = "Bạn đã thay đổi tên đăng nhập, vui lòng nhập mật khẩu" });
                                    }
                                    else
                                    {
                                        _logger.LogDebug("Username khớp với giá trị đã lưu trong database sau khi mã hóa form");
                                    }
                                }
                                catch (Exception ex2) {
                                    _logger.LogWarning(ex2, "Lỗi khi mã hóa username form: {0}", ex2.Message);
                                    
                                    // Nếu không thể so sánh, hãy giả định username đã thay đổi
                                    return new JsonResult(new { success = false, message = "Không thể xác thực tên đăng nhập, vui lòng nhập mật khẩu" });
                                }
                            }
                        }
                    }
                    else
                    {
                        return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản" });
                    }
                }
                
                // Kiểm tra lại nếu vẫn không có mật khẩu (thêm mới hoặc không tìm thấy tài khoản để lấy mật khẩu)
                if (string.IsNullOrWhiteSpace(password))
                {
                    return new JsonResult(new { success = false, message = "Vui lòng nhập mật khẩu Steam" });
                }

                string usernameHint = username.Length > 3 ? username.Substring(0, 3) + "***" : "***";
                _logger.LogInformation("Đang quét danh sách game cho tài khoản {Username}", usernameHint);

                // Sử dụng ScanAccountGames
                var games = await _steamAppInfoService.ScanAccountGames(username, password);

                if (games.Count == 0)
                {
                    return new JsonResult(new { 
                        success = false, 
                        message = "Không tìm thấy game nào trong tài khoản. Vui lòng kiểm tra lại thông tin đăng nhập hoặc tài khoản có game.",
                        results = new List<object>(),
                        count = 0
                    });
                }

                // Create results
                var results = games.Select(g => new {
                    appId = g.AppId,
                    gameName = g.GameName
                }).ToList();

                _logger.LogInformation("Đã lấy được {Count} game từ tài khoản {Username}", results.Count, usernameHint);

                return new JsonResult(new { 
                    success = true, 
                    results,
                    gameNames = string.Join(", ", results.Select(r => ((dynamic)r).gameName).ToList()),
                    appIds = string.Join(",", results.Select(r => ((dynamic)r).appId).ToList()),
                    count = results.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi quét danh sách game từ tài khoản {Username}: {Error}", 
                    username.Length > 3 ? username.Substring(0, 3) + "***" : "***", 
                    ex.Message);
                    
                string errorMessage = ex.Message;
                
                // Thêm thông tin chi tiết về lỗi
                if (ex.Message.Contains("InvalidPassword"))
                {
                    errorMessage = "Mật khẩu không đúng. Vui lòng kiểm tra lại.";
                }
                else if (ex.Message.Contains("InvalidName"))
                {
                    errorMessage = "Tên đăng nhập không đúng. Vui lòng kiểm tra lại.";
                }
                else if (ex.Message.Contains("timeout") || ex.Message.Contains("timed out"))
                {
                    errorMessage = "Quá thời gian kết nối đến Steam. Vui lòng thử lại sau.";
                }
                
                return new JsonResult(new { 
                    success = false, 
                    message = $"Lỗi khi quét tài khoản: {errorMessage}",
                    results = new List<object>(),
                    count = 0
                });
            }
        }
    }
}