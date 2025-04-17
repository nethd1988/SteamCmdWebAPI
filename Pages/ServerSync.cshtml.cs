using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class ServerSyncPageModel : PageModel
    {
        private readonly ILogger<ServerSyncPageModel> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly TcpClientService _tcpClientService;
        private readonly ProfileService _profileService;
        private readonly ServerSyncService _serverSyncService;

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public bool IsServerConfigured { get; set; }
        // Ẩn địa chỉ server
        private string ServerAddress { get; set; }
        private int ServerPort { get; set; }
        public DateTime? LastSyncTime { get; set; }
        public List<string> AvailableProfiles { get; set; } = new List<string>();

        public ServerSyncPageModel(
            ILogger<ServerSyncPageModel> logger,
            ServerSettingsService serverSettingsService,
            TcpClientService tcpClientService,
            ProfileService profileService,
            ServerSyncService serverSyncService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serverSettingsService = serverSettingsService ?? throw new ArgumentNullException(nameof(serverSettingsService));
            _tcpClientService = tcpClientService ?? throw new ArgumentNullException(nameof(tcpClientService));
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _serverSyncService = serverSyncService ?? throw new ArgumentNullException(nameof(serverSyncService));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            await LoadServerSettingsAsync();

            if (IsServerConfigured)
            {
                await LoadAvailableProfilesAsync();
            }

            return Page();
        }

        private async Task LoadServerSettingsAsync()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();
                IsServerConfigured = settings.EnableServerSync && !string.IsNullOrEmpty(settings.ServerAddress);
                ServerAddress = settings.ServerAddress;
                ServerPort = settings.ServerPort;
                LastSyncTime = settings.LastSyncTime;

                _logger.LogInformation("Loaded server settings: Server={Server}, Port={Port}, Enabled={Enabled}",
                    ServerAddress, ServerPort, IsServerConfigured);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading server settings");
                IsServerConfigured = false;
                StatusMessage = "Không thể tải cài đặt server: " + ex.Message;
                IsSuccess = false;
            }
        }

        private async Task LoadAvailableProfilesAsync()
        {
            try
            {
                AvailableProfiles.Clear();

                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (!serverSettings.EnableServerSync || string.IsNullOrEmpty(serverSettings.ServerAddress))
                {
                    _logger.LogWarning("Server sync is not enabled or server address is empty");
                    return;
                }

                _logger.LogInformation("Loading available profiles from server");

                // Sử dụng service mới để lấy danh sách profile
                var profileNames = await _serverSyncService.GetProfileNamesFromServerAsync();

                AvailableProfiles.AddRange(profileNames);
                _logger.LogInformation("Loaded {Count} profiles from server", AvailableProfiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading available profiles from server");
                StatusMessage = "Lỗi khi tải danh sách profile từ server: " + ex.Message;
                IsSuccess = false;
            }
        }

        public async Task<IActionResult> OnPostSyncAllAsync()
        {
            try
            {
                _logger.LogInformation("Starting sync of all profiles from server");

                await LoadServerSettingsAsync();
                if (!IsServerConfigured)
                {
                    StatusMessage = "Đồng bộ với server chưa được bật. Vui lòng kiểm tra cài đặt server.";
                    IsSuccess = false;
                    return Page();
                }

                // Sử dụng service mới để đồng bộ
                bool success = await _serverSyncService.AutoSyncWithServerAsync();

                if (success)
                {
                    StatusMessage = "Đã đồng bộ tất cả profile từ server thành công.";
                    IsSuccess = true;
                }
                else
                {
                    StatusMessage = "Đồng bộ không thành công. Vui lòng kiểm tra kết nối.";
                    IsSuccess = false;
                }

                await LoadServerSettingsAsync();
                await LoadAvailableProfilesAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing all profiles from server");
                StatusMessage = $"Lỗi khi đồng bộ: {ex.Message}";
                IsSuccess = false;
                await LoadServerSettingsAsync();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostSyncSingleAsync(string profileName)
        {
            try
            {
                _logger.LogInformation("Starting sync of profile '{ProfileName}' from server", profileName);

                if (string.IsNullOrEmpty(profileName))
                {
                    StatusMessage = "Vui lòng chọn một profile";
                    IsSuccess = false;
                    await LoadServerSettingsAsync();
                    await LoadAvailableProfilesAsync();
                    return Page();
                }

                await LoadServerSettingsAsync();
                if (!IsServerConfigured)
                {
                    StatusMessage = "Đồng bộ với server chưa được bật. Vui lòng kiểm tra cài đặt server.";
                    IsSuccess = false;
                    return Page();
                }

                // Lấy chi tiết profile từ server
                var serverProfile = await _serverSyncService.GetProfileFromServerByNameAsync(profileName);

                if (serverProfile == null)
                {
                    StatusMessage = $"Không tìm thấy profile '{profileName}' trên server";
                    IsSuccess = false;
                    await LoadAvailableProfilesAsync();
                    return Page();
                }

                // Kiểm tra xem profile đã tồn tại chưa
                var currentProfiles = await _profileService.GetAllProfiles();
                var existingProfile = currentProfiles.FirstOrDefault(p => p.Name == profileName);

                if (existingProfile != null)
                {
                    // Cập nhật profile hiện có
                    serverProfile.Id = existingProfile.Id;
                    serverProfile.Status = existingProfile.Status;
                    serverProfile.Pid = existingProfile.Pid;
                    serverProfile.StartTime = existingProfile.StartTime;
                    serverProfile.StopTime = existingProfile.StopTime;
                    serverProfile.LastRun = existingProfile.LastRun;

                    await _profileService.UpdateProfile(serverProfile);
                    StatusMessage = $"Đã cập nhật profile '{profileName}' từ server";
                }
                else
                {
                    // Thêm profile mới
                    int newId = currentProfiles.Count > 0 ? currentProfiles.Max(p => p.Id) + 1 : 1;
                    serverProfile.Id = newId;
                    serverProfile.Status = "Stopped";

                    await _profileService.AddProfileAsync(serverProfile);
                    StatusMessage = $"Đã thêm profile '{profileName}' từ server";
                }

                IsSuccess = true;

                // Cập nhật thời gian đồng bộ
                await _serverSettingsService.UpdateLastSyncTimeAsync();

                await LoadServerSettingsAsync();
                await LoadAvailableProfilesAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing profile '{ProfileName}' from server", profileName);
                StatusMessage = $"Lỗi khi đồng bộ profile '{profileName}': {ex.Message}";
                IsSuccess = false;
                await LoadServerSettingsAsync();
                await LoadAvailableProfilesAsync();
                return Page();
            }
        }

        public async Task<IActionResult> OnPostUploadToServerAsync()
        {
            try
            {
                _logger.LogInformation("Starting upload profiles to server");

                await LoadServerSettingsAsync();
                if (!IsServerConfigured)
                {
                    StatusMessage = "Đồng bộ với server chưa được bật. Vui lòng kiểm tra cài đặt server.";
                    IsSuccess = false;
                    return Page();
                }

                // Sử dụng service mới để đồng bộ
                bool success = await _serverSyncService.AutoSyncWithServerAsync();

                if (success)
                {
                    StatusMessage = "Đã đồng bộ tất cả profiles với server thành công";
                    IsSuccess = true;
                }
                else
                {
                    StatusMessage = "Đồng bộ lên server không thành công";
                    IsSuccess = false;
                }

                await LoadServerSettingsAsync();
                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading profiles to server");
                StatusMessage = $"Lỗi khi đồng bộ profiles lên server: {ex.Message}";
                IsSuccess = false;
                await LoadServerSettingsAsync();
                return Page();
            }
        }
    }
}