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
            EncryptionService encryptionService)
        {
            _logger = logger;
            _accountService = accountService;
            _steamApiService = steamApiService;
            _encryptionService = encryptionService;
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
                StatusMessage = $"Lỗi khi tải danh sách tài khoản: {ex.Message}";
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
                    return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản" });
                }

                // Không trả về mật khẩu đã giải mã
                account.Password = null;

                return new JsonResult(new { success = true, account });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin tài khoản {AccountId}", id);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostAddAccountAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Account.ProfileName) || string.IsNullOrWhiteSpace(Account.Username) || string.IsNullOrWhiteSpace(Account.Password))
                {
                    return new JsonResult(new { success = false, message = "Vui lòng điền đầy đủ thông tin bắt buộc" });
                }

                // Lấy tên game từ AppIds nếu có
                if (!string.IsNullOrWhiteSpace(Account.AppIds))
                {
                    var appIds = Account.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    var gameNames = new List<string>();
                    
                    foreach (var appId in appIds)
                    {
                        var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                        gameNames.Add(appInfo?.Name ?? $"AppID {appId}");
                    }
                    
                    Account.GameNames = string.Join(", ", gameNames);
                }

                await _accountService.AddAccountAsync(Account);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm tài khoản");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostUpdateAccountAsync()
        {
            try
            {
                if (Account.Id <= 0)
                {
                    return new JsonResult(new { success = false, message = "ID tài khoản không hợp lệ" });
                }

                var existingAccount = await _accountService.GetAccountByIdAsync(Account.Id);
                if (existingAccount == null)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản" });
                }

                // Giữ nguyên mật khẩu cũ nếu không có mật khẩu mới
                if (string.IsNullOrWhiteSpace(Account.Password))
                {
                    Account.Password = existingAccount.Password;
                }

                // Cập nhật tên game nếu AppIds thay đổi
                if (Account.AppIds != existingAccount.AppIds)
                {
                    var appIds = Account.AppIds.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrEmpty(id)).ToList();
                    var gameNames = new List<string>();
                    
                    foreach (var appId in appIds)
                    {
                        var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                        gameNames.Add(appInfo?.Name ?? $"AppID {appId}");
                    }
                    
                    Account.GameNames = string.Join(", ", gameNames);
                }
                else
                {
                    Account.GameNames = existingAccount.GameNames;
                }

                // Cập nhật thông tin khác
                Account.CreatedAt = existingAccount.CreatedAt;

                await _accountService.UpdateAccountAsync(Account);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật tài khoản {AccountId}", Account.Id);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostDeleteAccountAsync(int id)
        {
            try
            {
                if (id <= 0)
                {
                    return new JsonResult(new { success = false, message = "ID tài khoản không hợp lệ" });
                }

                var account = await _accountService.GetAccountByIdAsync(id);
                if (account == null)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy tài khoản cần xóa" });
                }

                await _accountService.DeleteAccountAsync(id);
                _logger.LogInformation("Đã xóa tài khoản ID {Id} thành công", id);
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa tài khoản {AccountId}", id);
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }
    }
}