using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using Microsoft.AspNetCore.Authorization;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly ILogger<AuthController> _logger;
        private readonly UserService _userService;

        public AuthController(ILogger<AuthController> logger, UserService userService)
        {
            _logger = logger;
            _userService = userService;
        }

        [HttpGet("check-session")]
        [AllowAnonymous]
        public IActionResult CheckSession()
        {
            try
            {
                bool isAuthenticated = User?.Identity?.IsAuthenticated ?? false;
                string username = null;
                bool isValid = false;

                if (isAuthenticated)
                {
                    username = User.Identity.Name;
                    var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                    // Kiểm tra hợp lệ
                    if (!string.IsNullOrEmpty(userId) && int.TryParse(userId, out int id))
                    {
                        var user = _userService.GetUserById(id);
                        isValid = user != null;
                    }

                    // Ghi log
                    if (isValid)
                    {
                        _logger.LogDebug("Kiểm tra session: Người dùng {Username} hợp lệ", username);
                    }
                    else
                    {
                        _logger.LogWarning("Kiểm tra session: Người dùng {Username} KHÔNG hợp lệ", username);
                    }
                }

                return Ok(new
                {
                    authenticated = isAuthenticated && isValid,
                    username = isAuthenticated && isValid ? username : null,
                    valid = isValid,
                    timestamp = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra phiên đăng nhập");
                return StatusCode(500, new { error = "Lỗi máy chủ nội bộ" });
            }
        }

        [HttpGet("status")]
        [AllowAnonymous]
        public IActionResult Status()
        {
            try
            {
                bool hasUsers = _userService.AnyUsers();
                bool isAuthenticated = User?.Identity?.IsAuthenticated ?? false;

                return Ok(new
                {
                    status = "online",
                    hasUsers = hasUsers,
                    authenticated = isAuthenticated,
                    serverTime = DateTime.Now
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra trạng thái");
                return StatusCode(500, new { error = "Lỗi máy chủ nội bộ" });
            }
        }
    }
}