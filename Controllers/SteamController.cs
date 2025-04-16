using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using System;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class SteamController : ControllerBase
    {
        private readonly ILogger<SteamController> _logger;
        private readonly SteamCmdService _steamCmdService;
        private readonly ProfileService _profileService;

        public SteamController(
            ILogger<SteamController> logger,
            SteamCmdService steamCmdService,
            ProfileService profileService)
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
            _profileService = profileService;
        }

        [HttpPost("Run/{id}")]
        public async Task<IActionResult> RunProfile(int id)
        {
            try
            {
                _logger.LogInformation("API - Yêu cầu chạy profile ID {ProfileId}", id);

                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    _logger.LogWarning("API - Không tìm thấy profile với ID {ProfileId}", id);
                    return NotFound(new { success = false, error = $"Không tìm thấy profile với ID {id}" });
                }

                bool success = await _steamCmdService.RunProfileAsync(id);
                return Ok(new { success = success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API - Lỗi khi chạy profile {ProfileId}", id);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("Stop/{id}")]
        public async Task<IActionResult> StopProfile(int id)
        {
            try
            {
                _logger.LogInformation("API - Yêu cầu dừng profile ID {ProfileId}", id);

                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    _logger.LogWarning("API - Không tìm thấy profile với ID {ProfileId}", id);
                    return NotFound(new { success = false, error = $"Không tìm thấy profile với ID {id}" });
                }

                await _steamCmdService.StopProfileAsync(id);

                // Cập nhật trạng thái profile
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);

                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API - Lỗi khi dừng profile {ProfileId}", id);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("RunAll")]
        public async Task<IActionResult> RunAllProfiles()
        {
            try
            {
                _logger.LogInformation("API - Yêu cầu chạy tất cả profile");
                await _steamCmdService.RunAllProfilesAsync();
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API - Lỗi khi chạy tất cả profile");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("StopAll")]
        public async Task<IActionResult> StopAllProfiles()
        {
            try
            {
                _logger.LogInformation("API - Yêu cầu dừng tất cả profile");
                await _steamCmdService.StopAllProfilesAsync();
                return Ok(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API - Lỗi khi dừng tất cả profile");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("Status/{id}")]
        public async Task<IActionResult> GetProfileStatus(int id)
        {
            try
            {
                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    return NotFound(new { success = false, error = $"Không tìm thấy profile với ID {id}" });
                }

                return Ok(new { 
                    success = true, 
                    status = profile.Status,
                    lastRun = profile.LastRun,
                    id = profile.Id,
                    name = profile.Name
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "API - Lỗi khi lấy trạng thái profile {ProfileId}", id);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
    }
}