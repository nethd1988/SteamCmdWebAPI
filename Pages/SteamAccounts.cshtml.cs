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

                // Don't return decrypted password
                account.Password = null;

                return new JsonResult(new { success = true, account });
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
                if (string.IsNullOrWhiteSpace(username))
                {
                    return new JsonResult(new { success = false, message = "Vui lòng nhập tên đăng nhập Steam" });
                }

                // Nếu là edit tài khoản (có accountId) và không có mật khẩu, lấy mật khẩu hiện tại
                if (accountId.HasValue && accountId.Value > 0 && string.IsNullOrWhiteSpace(password))
                {
                    var existingAccount = await _accountService.GetAccountByIdAsync(accountId.Value);
                    if (existingAccount != null)
                    {
                        _logger.LogInformation("Sử dụng mật khẩu đã lưu cho tài khoản ID {AccountId}", accountId.Value);
                        password = existingAccount.Password;
                        
                        // Kiểm tra xem tên đăng nhập có thay đổi không (nếu đã đổi thì yêu cầu nhập lại mật khẩu)
                        if (username != existingAccount.Username)
                        {
                            return new JsonResult(new { success = false, message = "Bạn đã thay đổi tên đăng nhập, vui lòng nhập mật khẩu" });
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

                _logger.LogInformation("Đang quét danh sách game cho tài khoản {Username}", username);

                // Phản hồi trạng thái ban đầu để người dùng biết quá trình đang tiến hành
                // Đây là thông báo về việc đang xử lý, thực tế cần triển khai SignalR để thực sự cập nhật trạng thái theo thời gian thực

                // Get the games list from the Steam account
                var games = await _steamAppInfoService.GetOwnedGamesAsync(username, password);

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

                _logger.LogInformation("Đã lấy được {Count} game từ tài khoản {Username}", results.Count, username);

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
                _logger.LogError(ex, "Lỗi khi quét danh sách game từ tài khoản {Username}: {Error}", username, ex.Message);
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