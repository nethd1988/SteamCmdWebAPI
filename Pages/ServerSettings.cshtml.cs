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
    public class ServerSettingsPageModel : PageModel
    {
        private readonly ILogger<ServerSettingsPageModel> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly TcpClientService _tcpClientService;
        private readonly ServerSyncService _serverSyncService;
        private readonly ProfileService _profileService;

        [BindProperty]
        public ServerSettings Settings { get; set; }

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public ServerSettingsPageModel(
            ILogger<ServerSettingsPageModel> logger,
            ServerSettingsService serverSettingsService,
            TcpClientService tcpClientService,
            ServerSyncService serverSyncService,
            ProfileService profileService)
        {
            _logger = logger;
            _serverSettingsService = serverSettingsService;
            _tcpClientService = tcpClientService;
            _serverSyncService = serverSyncService;
            _profileService = profileService;
            Settings = new ServerSettings();
        }

        public async Task OnGetAsync()
        {
            Settings = await _serverSettingsService.LoadSettingsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            try
            {
                // Lấy cài đặt hiện tại
                var currentSettings = await _serverSettingsService.LoadSettingsAsync();

                // Chỉ lưu thay đổi trạng thái EnableServerSync
                currentSettings.EnableServerSync = Settings.EnableServerSync;

                await _serverSettingsService.SaveSettingsAsync(currentSettings);

                // Nếu bật đồng bộ, chạy đồng bộ tự động một lần
                if (currentSettings.EnableServerSync)
                {
                    await _serverSyncService.AutoSyncWithServerAsync();

                    // Thêm đoạn đồng bộ âm thầm lên server
                    try
                    {
                        var profiles = await _profileService.GetAllProfiles();
                        await _serverSyncService.UploadProfilesToServerAsync(profiles);
                        _logger.LogInformation("Đã hoàn thành đồng bộ lên server khi lưu cài đặt");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi đồng bộ lên server khi lưu cài đặt");
                    }
                }

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
                // Lấy cài đặt hiện tại
                var settings = await _serverSettingsService.LoadSettingsAsync();

                // Kiểm tra kết nối bằng địa chỉ mặc định (bỏ qua giá trị nhập)
                bool isConnected = await _tcpClientService.TestConnectionAsync(settings.ServerAddress, settings.ServerPort);

                if (isConnected)
                {
                    // Cập nhật trạng thái kết nối
                    settings.ConnectionStatus = "Connected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Connected");
                    StatusMessage = $"Kết nối thành công tới server";
                    IsSuccess = true;
                }
                else
                {
                    // Cập nhật trạng thái kết nối
                    settings.ConnectionStatus = "Disconnected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Disconnected");
                    StatusMessage = $"Không thể kết nối tới server. Vui lòng kiểm tra kết nối mạng của bạn";
                    IsSuccess = false;
                }

                return Page();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi kết nối: {ex.Message}";
                IsSuccess = false;

                // Cập nhật trạng thái kết nối thành lỗi
                var settings = await _serverSettingsService.LoadSettingsAsync();
                settings.ConnectionStatus = "Error";
                await _serverSettingsService.UpdateConnectionStatusAsync("Error");

                return Page();
            }
        }

        public async Task<JsonResult> OnGetTestConnectionJsonAsync()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();

                // Kiểm tra kết nối bằng địa chỉ mặc định (bỏ qua giá trị nhập)
                bool isConnected = await _tcpClientService.TestConnectionAsync(settings.ServerAddress, settings.ServerPort);

                if (isConnected)
                {
                    // Cập nhật trạng thái kết nối
                    settings.ConnectionStatus = "Connected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Connected");
                    return new JsonResult(new { success = true, message = "Kết nối thành công" });
                }
                else
                {
                    // Cập nhật trạng thái kết nối
                    settings.ConnectionStatus = "Disconnected";
                    await _serverSettingsService.UpdateConnectionStatusAsync("Disconnected");
                    return new JsonResult(new { success = false, error = "Không thể kết nối tới server" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi kiểm tra kết nối JSON");
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostTestProfilesAsync()
        {
            try
            {
                // Lấy cài đặt hiện tại
                var settings = await _serverSettingsService.LoadSettingsAsync();

                var profileNames = await _tcpClientService.GetProfileNamesAsync(settings.ServerAddress, settings.ServerPort);
                StatusMessage = $"Kết nối thành công. Đã nhận {profileNames.Count} profile từ server.";
                IsSuccess = true;

                // Cập nhật trạng thái kết nối thành công
                settings.ConnectionStatus = "Connected";
                await _serverSettingsService.UpdateConnectionStatusAsync("Connected");

                // Kích hoạt đồng bộ tự động
                await _serverSyncService.AutoSyncWithServerAsync();

                return Page();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi khi lấy danh sách profile: {ex.Message}";
                IsSuccess = false;

                // Cập nhật trạng thái kết nối thành lỗi
                var settings = await _serverSettingsService.LoadSettingsAsync();
                settings.ConnectionStatus = "Error";
                await _serverSettingsService.UpdateConnectionStatusAsync("Error");

                return Page();
            }
        }
    }
}