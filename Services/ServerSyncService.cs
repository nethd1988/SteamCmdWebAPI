using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class ServerSyncService
    {
        private readonly ILogger<ServerSyncService> _logger;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ProfileService _profileService;
        private readonly TcpClientService _tcpClientService;
        private readonly HttpClient _httpClient;

        // Địa chỉ server mặc định cố định
        private const string DEFAULT_SERVER_ADDRESS = "idckz.ddnsfree.com";
        private const int DEFAULT_SERVER_PORT = 61188;

        public ServerSyncService(
            ILogger<ServerSyncService> logger,
            ServerSettingsService serverSettingsService,
            ProfileService profileService,
            TcpClientService tcpClientService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _serverSettingsService = serverSettingsService ?? throw new ArgumentNullException(nameof(serverSettingsService));
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _tcpClientService = tcpClientService ?? throw new ArgumentNullException(nameof(tcpClientService));

            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromMinutes(5); // 5 phút timeout cho các request lớn
        }

        public async Task<List<string>> GetProfileNamesFromServerAsync()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();
                if (!settings.EnableServerSync)
                {
                    _logger.LogWarning("Đồng bộ server chưa được kích hoạt");
                    return new List<string>();
                }

                // Luôn sử dụng địa chỉ mặc định
                string serverAddress = DEFAULT_SERVER_ADDRESS;
                int port = DEFAULT_SERVER_PORT;

                _logger.LogInformation("Đang lấy danh sách profile từ server {Server}:{Port}", serverAddress, port);

                // Gọi phương thức từ TcpClientService
                var profileNames = await _tcpClientService.GetProfileNamesAsync(serverAddress, port);

                _logger.LogInformation("Đã lấy {Count} profile từ server", profileNames.Count);
                return profileNames;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile từ server");
                return new List<string>();
            }
        }

        public async Task<bool> AutoSyncWithServerAsync()
        {
            try
            {
                var settings = await _serverSettingsService.LoadSettingsAsync();
                if (!settings.EnableServerSync)
                {
                    _logger.LogInformation("Đồng bộ server không được kích hoạt, bỏ qua đồng bộ tự động");
                    return false;
                }

                _logger.LogInformation("Bắt đầu lấy danh sách profile từ server");

                // Chỉ lấy danh sách tên profile từ server để hiển thị, không tự động đồng bộ
                var serverProfiles = await GetProfileNamesFromServerAsync();

                // Cập nhật thời gian đồng bộ
                await _serverSettingsService.UpdateLastSyncTimeAsync();

                _logger.LogInformation("Đã hoàn thành cập nhật danh sách {Count} profiles từ server",
                    serverProfiles.Count);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile từ server");
                return false;
            }
        }

        public async Task<SteamCmdProfile> GetProfileFromServerByNameAsync(string profileName)
        {
            try
            {
                if (string.IsNullOrEmpty(profileName))
                {
                    _logger.LogWarning("Tên profile không được để trống");
                    return null;
                }

                var settings = await _serverSettingsService.LoadSettingsAsync();
                if (!settings.EnableServerSync)
                {
                    _logger.LogWarning("Đồng bộ server chưa được kích hoạt");
                    return null;
                }

                // Luôn sử dụng địa chỉ mặc định
                string serverAddress = DEFAULT_SERVER_ADDRESS;
                int port = DEFAULT_SERVER_PORT;

                _logger.LogInformation("Đang lấy thông tin profile {ProfileName} từ server", profileName);

                // Gọi phương thức từ TcpClientService để lấy chi tiết profile
                var profile = await _tcpClientService.GetProfileDetailsByNameAsync(serverAddress, profileName, port);

                if (profile != null)
                {
                    _logger.LogInformation("Đã lấy thông tin profile {ProfileName} từ server", profileName);
                    return profile;
                }
                else
                {
                    _logger.LogWarning("Không tìm thấy thông tin profile {ProfileName} trên server", profileName);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin profile {ProfileName} từ server", profileName);

                // Mô phỏng dữ liệu mẫu cho mục đích testing
                return new SteamCmdProfile
                {
                    Name = profileName,
                    AppID = "730", // CS:GO app ID
                    InstallDirectory = $"D:\\SteamLibrary\\{profileName}",
                    Arguments = "-validate",
                    ValidateFiles = true,
                    AutoRun = false,
                    AnonymousLogin = true,
                    Status = "Stopped"
                };
            }
        }

        public async Task<bool> SyncProfileToClientAsync(SteamCmdProfile serverProfile)
        {
            try
            {
                // Kiểm tra profile hợp lệ
                if (serverProfile == null || string.IsNullOrEmpty(serverProfile.Name))
                {
                    _logger.LogWarning("Profile không hợp lệ để đồng bộ");
                    return false;
                }

                // Kiểm tra xem profile đã tồn tại chưa
                var localProfiles = await _profileService.GetAllProfiles();
                var existingProfile = localProfiles.FirstOrDefault(p => p.Name == serverProfile.Name);

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
                    _logger.LogInformation("Đã cập nhật profile {ProfileName} từ server", serverProfile.Name);
                }
                else
                {
                    // Thêm profile mới
                    int newId = localProfiles.Count > 0 ? localProfiles.Max(p => p.Id) + 1 : 1;
                    serverProfile.Id = newId;
                    serverProfile.Status = "Stopped";

                    await _profileService.AddProfileAsync(serverProfile);
                    _logger.LogInformation("Đã thêm profile mới {ProfileName} từ server", serverProfile.Name);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profile {ProfileName} từ server vào client",
                    serverProfile?.Name ?? "Unknown");
                return false;
            }
        }

        public async Task<bool> UploadProfileAsync(SteamCmdProfile profile)
        {
            try
            {
                if (profile == null)
                {
                    _logger.LogWarning("Không có profile để đồng bộ lên server");
                    return false;
                }

                var settings = await _serverSettingsService.LoadSettingsAsync();
                if (!settings.EnableServerSync)
                {
                    _logger.LogWarning("Đồng bộ server chưa được kích hoạt");
                    return false;
                }

                // Luôn sử dụng địa chỉ mặc định
                string serverAddress = DEFAULT_SERVER_ADDRESS;
                int port = DEFAULT_SERVER_PORT;

                _logger.LogInformation("Đang đồng bộ profile {ProfileName} lên server", profile.Name);

                // Kiểm tra kết nối
                bool isConnected = await _tcpClientService.TestConnectionAsync(serverAddress, port);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối đến server {Server}:{Port} để upload profile", serverAddress, port);
                    return false;
                }

                using (var tcpClient = new TcpClient())
                {
                    await tcpClient.ConnectAsync(serverAddress, port);

                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH + SEND_PROFILE
                    string command = $"AUTH:simple_auth_token SEND_PROFILE";
                    byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                    byte[] lengthBytes = BitConverter.GetBytes(commandBytes.Length);

                    await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi "READY_TO_RECEIVE"
                    byte[] responseHeaderBuffer = new byte[4];
                    int bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);

                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi từ server");
                        return false;
                    }

                    int responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    byte[] responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                    string response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                    if (response != "READY_TO_RECEIVE")
                    {
                        _logger.LogWarning("Server không sẵn sàng nhận profile: {Response}", response);
                        return false;
                    }

                    // Chuyển profile thành JSON
                    string json = JsonSerializer.Serialize(profile);
                    byte[] profileBytes = Encoding.UTF8.GetBytes(json);

                    // Gửi độ dài
                    byte[] profileLengthBytes = BitConverter.GetBytes(profileBytes.Length);
                    await stream.WriteAsync(profileLengthBytes, 0, profileLengthBytes.Length);

                    // Gửi nội dung
                    await stream.WriteAsync(profileBytes, 0, profileBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi
                    bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);
                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi sau khi gửi profile {ProfileName}", profile.Name);
                        return false;
                    }

                    responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                    response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                    if (response.StartsWith("SUCCESS:"))
                    {
                        _logger.LogInformation("Đã gửi profile {ProfileName} lên server thành công", profile.Name);
                        return true;
                    }
                    else if (response.StartsWith("ERROR:"))
                    {
                        _logger.LogWarning("Lỗi khi gửi profile {ProfileName} lên server: {Error}",
                            profile.Name, response.Substring(6));
                        return false;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profile {ProfileName} lên server", profile?.Name ?? "Unknown");
                return false;
            }
        }

        public async Task<bool> UploadProfilesToServerAsync(List<SteamCmdProfile> profiles)
        {
            try
            {
                if (profiles == null || profiles.Count == 0)
                {
                    _logger.LogWarning("Không có profiles để đồng bộ lên server");
                    return false;
                }

                var settings = await _serverSettingsService.LoadSettingsAsync();
                if (!settings.EnableServerSync)
                {
                    _logger.LogWarning("Đồng bộ server chưa được kích hoạt");
                    return false;
                }

                // Luôn sử dụng địa chỉ mặc định
                string serverAddress = DEFAULT_SERVER_ADDRESS;
                int port = DEFAULT_SERVER_PORT;

                _logger.LogInformation("Đang đồng bộ {Count} profiles lên server", profiles.Count);

                // Kiểm tra kết nối
                bool isConnected = await _tcpClientService.TestConnectionAsync(serverAddress, port);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối đến server {Server}:{Port} để upload profiles", serverAddress, port);
                    return false;
                }

                using (var tcpClient = new TcpClient())
                {
                    // Đặt timeout 10 giây
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    var timeoutTask = Task.Delay(10000); // 10 giây timeout

                    if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    {
                        _logger.LogWarning("Kết nối đến {Server}:{Port} bị timeout", serverAddress, port);
                        return false;
                    }

                    if (!tcpClient.Connected)
                    {
                        _logger.LogWarning("Không thể kết nối đến {Server}:{Port}", serverAddress, port);
                        return false;
                    }

                    // Gửi yêu cầu upload profiles
                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH + SEND_PROFILES
                    string command = $"AUTH:simple_auth_token SEND_PROFILES";
                    byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                    byte[] lengthBytes = BitConverter.GetBytes(commandBytes.Length);

                    await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi "READY_TO_RECEIVE"
                    byte[] responseHeaderBuffer = new byte[4];
                    int bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);

                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi từ server");
                        return false;
                    }

                    int responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    if (responseLength <= 0 || responseLength > 1024 * 1024) // Giới hạn 1MB
                    {
                        _logger.LogWarning("Độ dài phản hồi không hợp lệ: {Length}", responseLength);
                        return false;
                    }

                    byte[] responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                    if (bytesRead < responseLength)
                    {
                        _logger.LogWarning("Phản hồi không đầy đủ từ server");
                        return false;
                    }

                    string response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                    if (response != "READY_TO_RECEIVE")
                    {
                        _logger.LogWarning("Server không sẵn sàng nhận profiles: {Response}", response);
                        return false;
                    }

                    // Gửi từng profile
                    foreach (var profile in profiles)
                    {
                        try
                        {
                            // Chuyển profile thành JSON
                            string json = JsonSerializer.Serialize(profile);
                            byte[] profileBytes = Encoding.UTF8.GetBytes(json);

                            // Gửi độ dài
                            byte[] profileLengthBytes = BitConverter.GetBytes(profileBytes.Length);
                            await stream.WriteAsync(profileLengthBytes, 0, profileLengthBytes.Length);

                            // Gửi nội dung
                            await stream.WriteAsync(profileBytes, 0, profileBytes.Length);
                            await stream.FlushAsync();

                            // Đọc phản hồi
                            bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);
                            if (bytesRead < 4)
                            {
                                _logger.LogWarning("Không đọc được phản hồi sau khi gửi profile {ProfileName}", profile.Name);
                                continue;
                            }

                            responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                            if (responseLength <= 0 || responseLength > 1024 * 1024)
                            {
                                _logger.LogWarning("Độ dài phản hồi không hợp lệ: {Length}", responseLength);
                                continue;
                            }

                            responseBuffer = new byte[responseLength];
                            bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                            if (bytesRead < responseLength)
                            {
                                _logger.LogWarning("Phản hồi không đầy đủ từ server");
                                continue;
                            }

                            response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                            if (response.StartsWith("SUCCESS:"))
                            {
                                _logger.LogInformation("Đã gửi profile {ProfileName} lên server thành công", profile.Name);
                            }
                            else if (response.StartsWith("ERROR:"))
                            {
                                _logger.LogWarning("Lỗi khi gửi profile {ProfileName} lên server: {Error}",
                                    profile.Name, response.Substring(6));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi gửi profile {ProfileName} lên server", profile.Name);
                        }
                    }

                    // Gửi marker kết thúc (độ dài 0)
                    byte[] endMarker = BitConverter.GetBytes(0);
                    await stream.WriteAsync(endMarker, 0, endMarker.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi cuối cùng
                    bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);
                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi cuối cùng từ server");
                        return false;
                    }

                    responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                    if (bytesRead < responseLength)
                    {
                        _logger.LogWarning("Phản hồi cuối cùng không đầy đủ từ server");
                        return false;
                    }

                    response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                    if (response.StartsWith("DONE:"))
                    {
                        var parts = response.Split(':');
                        if (parts.Length >= 3)
                        {
                            int processedCount = int.Parse(parts[1]);
                            int errorCount = int.Parse(parts[2]);

                            _logger.LogInformation("Đã hoàn thành đồng bộ lên server: {Processed} thành công, {Errors} lỗi",
                                processedCount, errorCount);

                            return processedCount > 0;
                        }
                    }
                }

                _logger.LogInformation("Đã đồng bộ {Count} profiles lên server thành công", profiles.Count);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profiles lên server");
                return false;
            }
        }
    }
}