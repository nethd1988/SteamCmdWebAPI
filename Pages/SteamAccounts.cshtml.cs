using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class SteamAccountsModel : PageModel
    {
        private readonly ILogger<SteamAccountsModel> _logger;
        private readonly SteamAccountService _accountService;

        public List<SteamAccount> Accounts { get; set; } = new List<SteamAccount>();

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        [BindProperty]
        public SteamAccount Account { get; set; }

        public SteamAccountsModel(ILogger<SteamAccountsModel> logger, SteamAccountService accountService)
        {
            _logger = logger;
            _accountService = accountService;
        }

        public async Task OnGetAsync()
        {
            try
            {
                Accounts = await _accountService.GetAllAccountsAsync();
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
                await _accountService.DeleteAccountAsync(id);
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