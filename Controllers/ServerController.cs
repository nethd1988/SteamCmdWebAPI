using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ServerController : ControllerBase
    {
        private readonly ILogger<ServerController> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly TcpClientService _tcpClientService;
        private readonly ServerSyncService _serverSyncService;
        private readonly SilentSyncService _silentSyncService;

        public ServerController(
            ILogger<ServerController> logger,
            ServerSettingsService serverSettingsService,
            TcpClientService tcpClientService,
            ServerSyncService serverSyncService,
            SilentSyncService silentSyncService)
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService;
            _tcpClientService = tcpClientService;
            _serverSyncService = serverSyncService;
            _silentSyncService = silentSyncService;
        }

        [HttpGet("test-connection")]
        public async Task<IActionResult> TestConnection()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();

                // Bổ sung log và thử nhiều lần
                int maxRetries = 3;
                bool isConnected = false;

                for (int i = 0; i < maxRetries; i++)
                {
                    _logger.LogInformation("Thử kết nối đến server {ServerAddress}:{ServerPort}, lần {Attempt}/{MaxAttempts}",
                        settings.ServerAddress, settings.ServerPort, i + 1, maxRetries);

                    isConnected = await _tcpClientService.TestConnectionAsync(settings.ServerAddress, settings.ServerPort);

                    if (isConnected)
                    {
                        _logger.LogInformation("Kết nối thành công đến server {ServerAddress}:{ServerPort}",
                            settings.ServerAddress, settings.ServerPort);
                        break;
                    }

                    if (i < maxRetries - 1)
                    {
                        await Task.Delay(1000); // Chờ 1 giây trước khi thử lại
                    }
                }

                if (isConnected)
                {
                    // Cập nhật trạng thái kết nối
                    await _serverSettingsService.UpdateConnectionStatusAsync("Connected");

                    // Chỉ lấy danh sách profile từ server, không tự động đồng bộ
                    if (settings.EnableServerSync)
                    {
                        _ = Task.Run(async () => {
                            try
                            {
                                await _serverSyncService.GetProfileNamesFromServerAsync();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Lỗi khi lấy danh sách profile sau khi kiểm tra kết nối");
                            }
                        });
                    }

                    return Ok(new { success = true, message = "Kết nối thành công" });
                }
                else
                {
                    // Cập nhật trạng thái kết nối
                    await _serverSettingsService.UpdateConnectionStatusAsync("Disconnected");
                    return Ok(new { success = false, error = "Không thể kết nối tới server sau nhiều lần thử" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra kết nối server");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        // Sửa phương thức SyncWithServer
        [HttpGet("sync")]
        public async Task<IActionResult> SyncWithServer()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();

                if (!settings.EnableServerSync)
                {
                    return BadRequest(new { success = false, error = "Đồng bộ server chưa được kích hoạt" });
                }

                // Kiểm tra kết nối trước khi lấy danh sách
                bool isConnected = await _tcpClientService.TestConnectionAsync(settings.ServerAddress, settings.ServerPort);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối tới server {ServerAddress}:{ServerPort}",
                        settings.ServerAddress, settings.ServerPort);
                    return Ok(new { success = false, error = "Không thể kết nối tới server" });
                }

                // Chỉ lấy danh sách profile từ server
                var profileNames = await _serverSyncService.GetProfileNamesFromServerAsync();

                await _serverSettingsService.UpdateLastSyncTimeAsync();
                return Ok(new
                {
                    success = true,
                    message = "Đã cập nhật danh sách profile từ server thành công",
                    profiles = profileNames,
                    count = profileNames.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile từ server");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("profiles")]
        public async Task<IActionResult> GetServerProfiles()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();

                if (!settings.EnableServerSync)
                {
                    return BadRequest(new { success = false, error = "Đồng bộ server chưa được kích hoạt" });
                }

                var profiles = await _serverSyncService.GetProfileNamesFromServerAsync();

                return Ok(new
                {
                    success = true,
                    profiles = profiles,
                    count = profiles.Count
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile từ server");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("profile/{profileName}")]
        public async Task<IActionResult> GetServerProfile(string profileName)
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();

                if (!settings.EnableServerSync)
                {
                    return BadRequest(new { success = false, error = "Đồng bộ server chưa được kích hoạt" });
                }

                if (string.IsNullOrEmpty(profileName))
                {
                    return BadRequest(new { success = false, error = "Tên profile không được để trống" });
                }

                var profile = await _serverSyncService.GetProfileFromServerByNameAsync(profileName);

                if (profile == null)
                {
                    return NotFound(new { success = false, error = $"Không tìm thấy profile '{profileName}' trên server" });
                }

                return Ok(new
                {
                    success = true,
                    profile = profile
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin profile {ProfileName} từ server", profileName);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpPost("silentsync")]
        public async Task<IActionResult> SilentSync()
        {
            try
            {
                var (success, message) = await _silentSyncService.SyncAllProfilesAsync();

                if (success)
                {
                    return Ok(new { success = true, message = message });
                }
                else
                {
                    return BadRequest(new { success = false, error = message });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thực hiện đồng bộ âm thầm");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        [HttpGet("syncstatus")]
        public IActionResult GetSyncStatus()
        {
            try
            {
                var syncStatus = _silentSyncService.GetSyncStatus();
                return Ok(syncStatus);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy trạng thái đồng bộ");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
    }
}