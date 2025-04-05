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

        public ServerController(
            ILogger<ServerController> logger,
            ServerSettingsService serverSettingsService,
            TcpClientService tcpClientService,
            ServerSyncService serverSyncService)
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService;
            _tcpClientService = tcpClientService;
            _serverSyncService = serverSyncService;
        }

        [HttpGet("test-connection")]
        public async Task<IActionResult> TestConnection()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();
                bool isConnected = await _tcpClientService.TestConnectionAsync(settings.ServerAddress, settings.ServerPort);
                
                if (isConnected)
                {
                    // Cập nhật trạng thái kết nối
                    settings.ConnectionStatus = "Connected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Connected");
                    return Ok(new { success = true, message = "Kết nối thành công" });
                }
                else
                {
                    // Cập nhật trạng thái kết nối
                    settings.ConnectionStatus = "Disconnected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Disconnected");
                    return Ok(new { success = false, error = "Không thể kết nối tới server" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra kết nối server");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

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
                
                bool syncResult = await _serverSyncService.AutoSyncWithServerAsync();
                
                if (syncResult)
                {
                    return Ok(new { success = true, message = "Đồng bộ với server thành công" });
                }
                else
                {
                    return Ok(new { success = false, error = "Đồng bộ với server không thành công" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ với server");
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }
    }
}