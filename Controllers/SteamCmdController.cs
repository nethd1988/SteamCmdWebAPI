using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SteamCmdController : ControllerBase
    {
        private readonly ILogger<SteamCmdController> _logger;
        private readonly IProfileService _profileService;
        private readonly ISteamCmdService _steamCmdService;

        public SteamCmdController(ILogger<SteamCmdController> logger, IProfileService profileService, ISteamCmdService steamCmdService)
        {
            _logger = logger;
            _profileService = profileService;
            _steamCmdService = steamCmdService;
        }

        [HttpGet("status")]
        public async Task<IActionResult> GetStatus()
        {
            var profiles = await _profileService.GetProfiles();
            var groupedProfiles = new Dictionary<string, object>();

            foreach (var profile in profiles)
            {
                groupedProfiles[profile.Name] = new
                {
                    profile.Name,
                    profile.Status,
                    profile.AppID,
                    profile.LastRun,
                    profile.InstallDirectory,
                    profile.DependencyIDs
                };
            }

            return Ok(new { Profiles = groupedProfiles });
        }

        [HttpPost("clear-login-cache")]
        [ProducesResponseType(typeof(ApiResponse), StatusCodes.Status200OK)]
        public async Task<IActionResult> ClearLoginCache()
        {
            try
            {
                // Call the service method to clear the Steam login cache
                await _steamCmdService.ClearLoginCacheAsync();
                
                return Ok(new ApiResponse
                {
                    Success = true,
                    Message = "Đã xóa cache đăng nhập của Steam thành công"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa cache đăng nhập Steam");
                return BadRequest(new ApiResponse
                {
                    Success = false,
                    Message = $"Lỗi khi xóa cache đăng nhập Steam: {ex.Message}"
                });
            }
        }
    }
} 