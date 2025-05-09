using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System.Linq;
using System.Text.Json.Serialization;

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

        public async Task<IActionResult> OnPostUpdateAccountAsync([FromBody] UpdateAccountRequest request)
        {
            if (request == null || request.Account == null)
            {
                return new JsonResult(new { success = false, message = "Dữ liệu không hợp lệ" });
            }
            
            try
            {
                Account = request.Account;
                
                _logger.LogInformation("OnPostUpdateAccountAsync: Nhận yêu cầu cập nhật tài khoản {AccountId}, AutoScanEnabled: {AutoScanEnabled}, ScanInterval: {ScanInterval}",
                    Account.Id, Account.AutoScanEnabled, Account.ScanIntervalHours);
                
                // Không cần kiểm tra request.Form vì chúng ta đang dùng JSON binding
                
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

                // Sử dụng GameNames hiện tại nếu AppIds không thay đổi
                if (Account.AppIds == existingAccount.AppIds)
                {
                    Account.GameNames = existingAccount.GameNames;
                    _logger.LogInformation("AppIds không thay đổi, giữ nguyên GameNames");
                }
                // Chỉ cập nhật GameNames nếu AppIds thay đổi và AppIds không rỗng
                else if (!string.IsNullOrWhiteSpace(Account.AppIds))
                {
                    // Ghi log để theo dõi
                    _logger.LogInformation("AppIds thay đổi, cập nhật GameNames");
                    
                    // Thực hiện trong Task riêng biệt để không chặn luồng chính
                    await Task.Run(async () => {
                        try {
                            var appIds = Account.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                            var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(appIds);
                            var gameNames = new List<string>();
                            
                            foreach (var (appId, gameName) in gameInfos)
                            {
                                if (!string.IsNullOrEmpty(gameName))
                                {
                                    gameNames.Add(gameName);
                                }
                                else
                                {
                                    // Thử dùng SteamApiService nếu không có thông tin trong SteamAppInfoService
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
                        catch (Exception ex) {
                            _logger.LogError(ex, "Lỗi khi lấy thông tin game: {Error}", ex.Message);
                            // Nếu có lỗi, giữ GameNames cũ
                            Account.GameNames = existingAccount.GameNames;
                        }
                    });
                }
                else
                {
                    // Nếu AppIds rỗng, GameNames cũng rỗng
                    Account.GameNames = string.Empty;
                }

                // Update other info
                Account.CreatedAt = existingAccount.CreatedAt;
                Account.UpdatedAt = DateTime.Now;
                
                // Cập nhật thời gian quét tiếp theo nếu quét tự động được bật
                if (Account.AutoScanEnabled)
                {
                    // Nếu không có LastScanTime, thiết lập nó là thời gian hiện tại
                    Account.LastScanTime = existingAccount.LastScanTime ?? DateTime.Now;
                    
                    // Tính toán thời gian quét tiếp theo
                    Account.NextScanTime = DateTime.Now.AddHours(Account.ScanIntervalHours);
                    
                    _logger.LogInformation("Quét tự động được bật với chu kỳ {Hours} giờ. Quét tiếp theo: {NextScan}", 
                        Account.ScanIntervalHours, Account.NextScanTime);
                }
                else
                {
                    // Nếu bị tắt, xóa thời gian quét tiếp theo
                    Account.NextScanTime = null;
                    _logger.LogInformation("Quét tự động đã bị tắt");
                }

                // Lưu tài khoản ngay lập tức
                await _accountService.UpdateAccountAsync(Account);

                // Sau khi lưu xong, bắt đầu một Task riêng để cập nhật thông tin game
                if (Account.AppIds != existingAccount.AppIds && !string.IsNullOrWhiteSpace(Account.AppIds))
                {
                    // Chạy các nhiệm vụ dưới nền mà không chờ kết quả
                    Task.Run(async () => {
                        try 
                        {
                            _logger.LogInformation("Đang cập nhật thông tin game dưới nền...");
                            var updatedAccount = await _accountService.GetAccountByIdAsync(Account.Id);
                            
                            if (updatedAccount != null)
                            {
                                var appIds = updatedAccount.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                                var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(appIds);
                                
                                if (gameInfos.Count > 0)
                                {
                                    var gameNames = new List<string>();
                                    foreach (var (appId, gameName) in gameInfos)
                                    {
                                        if (!string.IsNullOrEmpty(gameName))
                                        {
                                            gameNames.Add(gameName);
                                        }
                                        else
                                        {
                                            gameNames.Add($"AppID {appId}");
                                        }
                                    }
                                    
                                    updatedAccount.GameNames = string.Join(", ", gameNames);
                                    await _accountService.UpdateAccountAsync(updatedAccount);
                                    _logger.LogInformation("Đã cập nhật thông tin game dưới nền thành công");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi cập nhật thông tin game dưới nền: {Error}", ex.Message);
                        }
                    });
                }

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

                // Kiểm tra xem tài khoản đã có AppIDs từ trước chưa
                List<string> existingAppIds = new List<string>();
                string existingGameNames = string.Empty;
                
                if (accountId.HasValue && accountId.Value > 0)
                {
                    var existingAccount = await _accountService.GetAccountByIdAsync(accountId.Value);
                    if (existingAccount != null && !string.IsNullOrWhiteSpace(existingAccount.AppIds))
                    {
                        existingAppIds = existingAccount.AppIds
                            .Split(',')
                            .Select(id => id.Trim())
                            .Where(id => !string.IsNullOrEmpty(id))
                            .ToList();
                            
                        existingGameNames = existingAccount.GameNames ?? string.Empty;
                        _logger.LogInformation("Tài khoản đã có sẵn {Count} AppIDs", existingAppIds.Count);
                    }
                }

                // Sử dụng ScanAccountGames với phương pháp cải tiến
                var games = await _steamAppInfoService.ScanAccountGames(username, password);

                if (games.Count == 0)
                {
                    if (existingAppIds.Count > 0)
                    {
                        // Nếu không quét được game mới nhưng tài khoản đã có sẵn các AppID
                        return new JsonResult(new { 
                            success = true,
                            message = "Không tìm thấy game mới nào, giữ nguyên danh sách game hiện tại.",
                            results = existingAppIds.Select(id => new {
                                appId = id,
                                gameName = "Game " + id
                            }).ToList(),
                            gameNames = existingGameNames,
                            appIds = string.Join(",", existingAppIds),
                            count = existingAppIds.Count
                        });
                    }
                    
                    return new JsonResult(new { 
                        success = false, 
                        message = "Không tìm thấy game nào trong tài khoản. Vui lòng kiểm tra lại thông tin đăng nhập hoặc tài khoản có game.",
                        results = new List<object>(),
                        count = 0
                    });
                }

                // Tạo danh sách kết quả từ các game đã quét được
                var results = games.Select(g => new {
                    appId = g.AppId,
                    gameName = g.GameName
                }).ToList();
                
                // Kiểm tra và kết hợp với AppIDs hiện có (chỉ với tài khoản đã tồn tại)
                if (existingAppIds.Count > 0)
                {
                    // Hợp nhất danh sách game từ scan mới và AppIDs hiện tại
                    var mergedAppIds = new HashSet<string>(existingAppIds);
                    
                    foreach (var game in games)
                    {
                        mergedAppIds.Add(game.AppId);
                    }
                    
                    // Sắp xếp lại danh sách để nhất quán
                    var sortedAppIds = mergedAppIds.OrderBy(id => id).ToList();
                    
                    // Thông báo về số lượng game mới phát hiện
                    int newGamesCount = sortedAppIds.Count - existingAppIds.Count;
                    
                    if (newGamesCount > 0)
                    {
                        _logger.LogInformation("Đã phát hiện {Count} game mới từ tài khoản {Username}", 
                            newGamesCount, usernameHint);
                        
                        // Lấy thông tin về tên game cho toàn bộ AppID đã hợp nhất
                        var gameInfos = await _steamAppInfoService.GetAppInfoBatchAsync(sortedAppIds);
                        
                        // Tạo danh sách kết quả mới với tên game cập nhật
                        results = gameInfos.Select(g => new {
                            appId = g.AppId,
                            gameName = g.GameName
                        }).ToList();
                        
                        return new JsonResult(new { 
                            success = true, 
                            message = $"Đã quét được {games.Count} game và phát hiện {newGamesCount} game mới!",
                            results,
                            gameNames = string.Join(", ", results.Select(r => ((dynamic)r).gameName).ToList()),
                            appIds = string.Join(",", sortedAppIds),
                            count = results.Count
                        });
                    }
                    else
                    {
                        _logger.LogInformation("Không phát hiện game mới từ tài khoản {Username}", usernameHint);
                        
                        // Nếu không có game mới, giữ nguyên danh sách hiện tại nhưng vẫn cập nhật lại với tên game mới nếu có
                        return new JsonResult(new { 
                            success = true, 
                            message = "Không phát hiện thêm game mới trong tài khoản.",
                            results,
                            gameNames = string.Join(", ", results.Select(r => ((dynamic)r).gameName).ToList()),
                            appIds = string.Join(",", existingAppIds),
                            count = existingAppIds.Count
                        });
                    }
                }

                _logger.LogInformation("Đã quét được {Count} game từ tài khoản {Username}", results.Count, usernameHint);

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
                else if (ex.Message.Contains("rate limit") || ex.Message.Contains("RateLimitExceeded"))
                {
                    errorMessage = "Giới hạn tốc độ kết nối đến Steam. Vui lòng thử lại sau ít phút.";
                }
                else if (ex.Message.Contains("Steam Guard") || ex.Message.Contains("2fa"))
                {
                    errorMessage = "Tài khoản yêu cầu xác thực Steam Guard. Không thể quét tự động.";
                }
                
                return new JsonResult(new { 
                    success = false, 
                    message = $"Lỗi khi quét tài khoản: {errorMessage}",
                    results = new List<object>(),
                    count = 0
                });
            }
        }

        public async Task<IActionResult> OnPostToggleAutoScanAsync([FromBody] ToggleAutoScanRequest request)
        {
            try
            {
                _logger.LogInformation("OnPostToggleAutoScanAsync: accountId={AccountId}, enabled={Enabled}", 
                    request.AccountId, request.Enabled);
                
                var account = await _accountService.GetAccountByIdAsync(request.AccountId);
                if (account == null)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản" });
                }

                // Cập nhật trạng thái quét tự động
                account.AutoScanEnabled = request.Enabled;
                
                // Nếu bật, tính toán thời gian quét tiếp theo
                if (request.Enabled)
                {
                    account.LastScanTime = account.LastScanTime ?? DateTime.Now;
                    account.NextScanTime = DateTime.Now.AddHours(account.ScanIntervalHours);
                }
                else
                {
                    account.NextScanTime = null;
                }
                
                await _accountService.UpdateAccountAsync(account);
                
                return new JsonResult(new 
                { 
                    success = true, 
                    nextScanTime = account.NextScanTime?.ToString("dd/MM/yyyy HH:mm"),
                    message = request.Enabled ? "Đã bật quét tự động" : "Đã tắt quét tự động"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thay đổi trạng thái quét tự động cho tài khoản {AccountId}", request.AccountId);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
        
        public async Task<IActionResult> OnPostScanNowAsync(int accountId, bool replaceExistingIds = false)
        {
            try
            {
                var account = await _accountService.GetAccountByIdAsync(accountId);
                if (account == null)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản" });
                }

                // Lấy appIds hiện tại
                var existingAppIds = string.IsNullOrEmpty(account.AppIds) 
                    ? new List<string>() 
                    : account.AppIds.Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList();
                
                int existingCount = existingAppIds.Count;
                
                // Kiểm tra thông tin đăng nhập
                if (string.IsNullOrEmpty(account.Username) || string.IsNullOrEmpty(account.Password))
                {
                    return new JsonResult(new { success = false, message = "Thiếu thông tin đăng nhập" });
                }
                
                // Quét game từ tài khoản
                _logger.LogInformation("Đang quét tài khoản {ProfileName} (ID: {AccountId}), replaceExistingIds: {ReplaceIds}", 
                    account.ProfileName, accountId, replaceExistingIds);
                
                var games = await _steamAppInfoService.ScanAccountGames(account.Username, account.Password);
                
                if (games.Count == 0)
                {
                    return new JsonResult(new { 
                        success = true, 
                        message = "Không tìm thấy game nào trong tài khoản",
                        newGamesCount = 0
                    });
                }
                
                // Lấy danh sách appIds mới
                var scannedAppIds = games.Select(g => g.AppId).ToList();
                
                // Kết hợp hoặc thay thế danh sách cũ tùy thuộc vào replaceExistingIds
                HashSet<string> allAppIds;
                int newGamesCount;
                
                if (replaceExistingIds)
                {
                    // Thay thế hoàn toàn danh sách cũ
                    allAppIds = new HashSet<string>(scannedAppIds);
                    newGamesCount = scannedAppIds.Count;
                    _logger.LogInformation("Thay thế danh sách game cũ với {Count} game mới", scannedAppIds.Count);
                }
                else
                {
                    // Kết hợp danh sách cũ và mới
                    allAppIds = new HashSet<string>(existingAppIds);
                    foreach (var appId in scannedAppIds)
                    {
                        allAppIds.Add(appId);
                    }
                    newGamesCount = allAppIds.Count - existingCount;
                    _logger.LogInformation("Kết hợp {ExistingCount} game cũ với {ScannedCount} game mới, tổng: {TotalCount}", 
                        existingCount, scannedAppIds.Count, allAppIds.Count);
                }
                
                // Cập nhật danh sách game
                account.AppIds = string.Join(",", allAppIds);
                
                // Cập nhật tên game
                var gameNames = new List<string>();
                foreach (var (appId, gameName) in games)
                {
                    if (!string.IsNullOrEmpty(gameName))
                    {
                        gameNames.Add(gameName);
                    }
                }
                
                // Nếu là thay thế, chỉ lấy tên game mới; nếu không thì kết hợp
                if (replaceExistingIds)
                {
                    account.GameNames = string.Join(",", gameNames);
                }
                else
                {
                    // Kết hợp tên game cũ và mới
                    var allGameNames = new HashSet<string>(gameNames);
                    if (!string.IsNullOrEmpty(account.GameNames))
                    {
                        foreach (var name in account.GameNames.Split(',').Select(n => n.Trim()).Where(n => !string.IsNullOrEmpty(n)))
                        {
                            allGameNames.Add(name);
                        }
                    }
                    account.GameNames = string.Join(",", allGameNames);
                }
                
                // Cập nhật thời gian quét
                account.LastScanTime = DateTime.Now;
                
                if (account.AutoScanEnabled)
                {
                    account.NextScanTime = DateTime.Now.AddHours(account.ScanIntervalHours);
                }
                
                await _accountService.UpdateAccountAsync(account);
                
                string resultMessage = replaceExistingIds
                    ? $"Đã quét thành công và thay thế với {newGamesCount} game"
                    : $"Đã quét thành công và tìm thấy {newGamesCount} game mới";
                
                return new JsonResult(new { 
                    success = true, 
                    message = resultMessage,
                    newGamesCount = newGamesCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi quét tài khoản {AccountId}", accountId);
                return new JsonResult(new { success = false, message = $"Lỗi khi quét: {ex.Message}" });
            }
        }

        public async Task<IActionResult> OnPostUpdateScanSettingsAsync([FromBody] UpdateAccountRequest request)
        {
            if (request == null || request.Account == null)
            {
                return new JsonResult(new { success = false, message = "Dữ liệu không hợp lệ" });
            }
            
            try
            {
                var accountToUpdate = request.Account;
                
                _logger.LogInformation("OnPostUpdateScanSettingsAsync: Đang cập nhật cài đặt quét tự động cho tài khoản ID {Id}, " +
                    "AutoScanEnabled: {AutoScanEnabled}, ScanIntervalHours: {ScanIntervalHours}",
                    accountToUpdate.Id, accountToUpdate.AutoScanEnabled, accountToUpdate.ScanIntervalHours);
                
                // Lấy tài khoản từ cơ sở dữ liệu
                var existingAccount = await _accountService.GetAccountByIdAsync(accountToUpdate.Id);
                if (existingAccount == null)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản" });
                }
                
                // Chỉ cập nhật các thông tin liên quan đến quét tự động
                existingAccount.AutoScanEnabled = accountToUpdate.AutoScanEnabled;
                existingAccount.ScanIntervalHours = accountToUpdate.ScanIntervalHours > 0 ? accountToUpdate.ScanIntervalHours : 6;
                
                // Cập nhật thời gian quét tiếp theo nếu bật quét tự động
                if (existingAccount.AutoScanEnabled)
                {
                    existingAccount.NextScanTime = DateTime.Now.AddHours(existingAccount.ScanIntervalHours);
                }
                
                // Cập nhật tài khoản trong cơ sở dữ liệu
                await _accountService.UpdateAccountAsync(existingAccount);
                
                _logger.LogInformation("OnPostUpdateScanSettingsAsync: Đã cập nhật thành công cài đặt quét tự động cho tài khoản ID {Id}", 
                    existingAccount.Id);
                
                // Trả về kết quả thành công và thời gian quét tiếp theo
                return new JsonResult(new { 
                    success = true, 
                    nextScanTime = existingAccount.NextScanTime?.ToString("dd/MM HH:mm") 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OnPostUpdateScanSettingsAsync: Lỗi khi cập nhật cài đặt quét tự động cho tài khoản ID {Id}", 
                    request.Account.Id);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
    }
    
    // Lớp request model cho ToggleAutoScan
    public class ToggleAutoScanRequest
    {
        public int AccountId { get; set; }
        public bool Enabled { get; set; }
    }
    
    // Lớp request model cho UpdateAccount
    public class UpdateAccountRequest
    {
        public SteamAccount Account { get; set; }
    }
}