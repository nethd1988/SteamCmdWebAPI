using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class ServerSettingsModel : PageModel
    {
        private readonly ILogger<ServerSettingsModel> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly TcpClientService _tcpClientService;

        [BindProperty]
        public ServerSettings Settings { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public ServerSettingsModel(
            ILogger<ServerSettingsModel> logger,
            ServerSettingsService serverSettingsService,
            TcpClientService tcpClientService)
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService;
            _tcpClientService = tcpClientService;
            Settings = new ServerSettings();
        }

        public async Task OnGetAsync()
        {
            Settings = await _serverSettingsService.LoadSettingsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            try
            {
                await _serverSettingsService.SaveSettingsAsync(Settings);
                StatusMessage = "Cài đặt server đã được lưu";
                IsSuccess = true;

                return RedirectToPage();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi lưu cài đặt: {ex.Message}";
                IsSuccess = false;
                return Page();
            }
        }

        public async Task<IActionResult> OnPostTestConnectionAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(Settings.ServerAddress))
                {
                    ModelState.AddModelError("Settings.ServerAddress", "Địa chỉ server không được để trống");
                    return Page();
                }

                bool isConnected = await _tcpClientService.TestConnectionAsync(Settings.ServerAddress, Settings.ServerPort);
                
                if (isConnected)
                {
                    // Cập nhật trạng thái kết nối
                    Settings.ConnectionStatus = "Connected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Connected");
                    StatusMessage = $"Kết nối thành công tới {Settings.ServerAddress}:{Settings.ServerPort}";
                    IsSuccess = true;
                }
                else
                {
                    // Cập nhật trạng thái kết nối
                    Settings.ConnectionStatus = "Disconnected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Disconnected");
                    StatusMessage = $"Không thể kết nối tới {Settings.ServerAddress}:{Settings.ServerPort}";
                    IsSuccess = false;
                }

                return Page();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi kết nối: {ex.Message}";
                IsSuccess = false;
                Settings.ConnectionStatus = "Error";
                await _serverSettingsService.UpdateConnectionStatusAsync("Error");
                return Page();
            }
        }

        public async Task<IActionResult> OnPostTestProfilesAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(Settings.ServerAddress))
                {
                    ModelState.AddModelError("Settings.ServerAddress", "Địa chỉ server không được để trống");
                    return Page();
                }

                var profileNames = await _tcpClientService.GetProfileNamesAsync(Settings.ServerAddress, Settings.ServerPort);
                StatusMessage = $"Kết nối thành công. Đã nhận {profileNames.Count} profile từ server.";
                IsSuccess = true;
                
                // Cập nhật trạng thái kết nối
                Settings.ConnectionStatus = "Connected";
                await _serverSettingsService.UpdateConnectionStatusAsync("Connected");

                return Page();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi lấy danh sách profile: {ex.Message}";
                IsSuccess = false;
                Settings.ConnectionStatus = "Error";
                await _serverSettingsService.UpdateConnectionStatusAsync("Error");
                return Page();
            }
        }
    }
}