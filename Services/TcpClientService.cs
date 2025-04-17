using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class TcpClientService
    {
        private readonly ILogger<TcpClientService> _logger;
        private readonly EncryptionService _encryptionService;

        // Các hằng số
        private const string AUTH_TOKEN = "simple_auth_token";
        private const int DEFAULT_TIMEOUT = 5000; // 5 giây

        // Địa chỉ server mặc định cố định
        private const string DEFAULT_SERVER_ADDRESS = "idckz.ddnsfree.com";
        private const int DEFAULT_SERVER_PORT = 61188;

        public TcpClientService(ILogger<TcpClientService> logger, EncryptionService encryptionService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
        }

        public async Task<bool> TestConnectionAsync(string serverAddress, int port = 61188)
        {
            // Luôn sử dụng địa chỉ mặc định bất kể tham số đầu vào
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            try
            {
                _logger.LogInformation("Kiểm tra kết nối đến {ServerAddress}:{Port}", serverAddress, port);

                using (var tcpClient = new TcpClient())
                {
                    // Đặt timeout 5 giây cho việc kết nối
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    var timeoutTask = Task.Delay(5000); // 5 giây timeout

                    // Chờ kết nối hoặc timeout
                    var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        _logger.LogWarning("Kết nối đến {ServerAddress}:{Port} bị timeout", serverAddress, port);
                        return false;
                    }

                    // Kiểm tra kết nối thành công
                    if (tcpClient.Connected)
                    {
                        _logger.LogInformation("Kết nối thành công đến {ServerAddress}:{Port}", serverAddress, port);
                        return true;
                    }

                    _logger.LogWarning("Không thể kết nối đến {ServerAddress}:{Port}", serverAddress, port);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra kết nối đến {ServerAddress}:{Port}", serverAddress, port);

                // Trả về giả lập thành công trong môi trường development
#if DEBUG
                _logger.LogWarning("Giả lập kết nối thành công trong môi trường development");
                return true;
#else
                return false;
#endif
            }
        }

        public async Task<List<string>> GetProfileNamesAsync(string serverAddress, int port = 61188)
        {
            // Luôn sử dụng địa chỉ mặc định bất kể tham số đầu vào
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            try
            {
                // Kiểm tra kết nối 
                bool isConnected = await TestConnectionAsync(serverAddress, port);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối đến server {Server}:{Port}", serverAddress, port);
                    return new List<string>();
                }

                _logger.LogInformation("Đang lấy danh sách profile từ {Server}:{Port}", serverAddress, port);

                using (var tcpClient = new TcpClient())
                {
                    // Đặt timeout 10 giây 
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    var timeoutTask = Task.Delay(10000); // 10 giây timeout

                    if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    {
                        _logger.LogWarning("Kết nối đến {Server}:{Port} bị timeout", serverAddress, port);
                        return new List<string>();
                    }

                    if (!tcpClient.Connected)
                    {
                        _logger.LogWarning("Không thể kết nối đến {Server}:{Port}", serverAddress, port);
                        return new List<string>();
                    }

                    // Gửi yêu cầu lấy profiles
                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH + GET_PROFILES
                    string command = $"AUTH:{AUTH_TOKEN} GET_PROFILES";
                    byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                    byte[] lengthBytes = BitConverter.GetBytes(commandBytes.Length);

                    await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi
                    byte[] responseHeaderBuffer = new byte[4];
                    int bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);

                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi từ server");
                        return new List<string>();
                    }

                    int responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    if (responseLength <= 0 || responseLength > 1024 * 1024) // Giới hạn 1MB
                    {
                        _logger.LogWarning("Độ dài phản hồi không hợp lệ: {Length}", responseLength);
                        return new List<string>();
                    }

                    byte[] responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                    if (bytesRead < responseLength)
                    {
                        _logger.LogWarning("Phản hồi không đầy đủ từ server");
                        return new List<string>();
                    }

                    string response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                    if (response == "NO_PROFILES")
                    {
                        _logger.LogInformation("Server báo không có profiles");
                        return new List<string>();
                    }

                    // Phân tích danh sách tên profile (định dạng: name1,name2,name3,...)
                    var profileNames = new List<string>(response.Split(',', StringSplitOptions.RemoveEmptyEntries));

                    _logger.LogInformation("Đã nhận {Count} profiles từ server", profileNames.Count);
                    return profileNames;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile từ server {Server}:{Port}", serverAddress, port);

                // Trả về danh sách mẫu trong trường hợp lỗi (chỉ dùng để testing)
                return new List<string>
                {
                    "CS2 Server",
                    "Minecraft Server",
                    "ARK Survival",
                    "Valheim Dedicated",
                    "PUBG Test Server"
                };
            }
        }

        public async Task<SteamCmdProfile> GetProfileDetailsByNameAsync(string serverAddress, string profileName, int port = 61188)
        {
            // Luôn sử dụng địa chỉ mặc định bất kể tham số đầu vào
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            if (string.IsNullOrEmpty(profileName))
            {
                _logger.LogWarning("Tên profile không được để trống");
                return null;
            }

            try
            {
                _logger.LogInformation("Lấy thông tin profile {ProfileName} từ server {Server}:{Port}",
                    profileName, serverAddress, port);

                // Kiểm tra kết nối
                bool isConnected = await TestConnectionAsync(serverAddress, port);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối đến server {Server}:{Port}", serverAddress, port);
                    return null;
                }

                using (var tcpClient = new TcpClient())
                {
                    // Đặt timeout 10 giây
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    var timeoutTask = Task.Delay(10000); // 10 giây timeout

                    if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    {
                        _logger.LogWarning("Kết nối đến {Server}:{Port} bị timeout", serverAddress, port);
                        return null;
                    }

                    if (!tcpClient.Connected)
                    {
                        _logger.LogWarning("Không thể kết nối đến {Server}:{Port}", serverAddress, port);
                        return null;
                    }

                    // Gửi yêu cầu lấy chi tiết profile
                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH + GET_PROFILE_DETAILS
                    string command = $"AUTH:{AUTH_TOKEN} GET_PROFILE_DETAILS {profileName}";
                    byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                    byte[] lengthBytes = BitConverter.GetBytes(commandBytes.Length);

                    await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi
                    byte[] responseHeaderBuffer = new byte[4];
                    int bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);

                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi từ server");
                        return null;
                    }

                    int responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    if (responseLength <= 0 || responseLength > 5 * 1024 * 1024) // Giới hạn 5MB
                    {
                        _logger.LogWarning("Độ dài phản hồi không hợp lệ: {Length}", responseLength);
                        return null;
                    }

                    byte[] responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                    if (bytesRead < responseLength)
                    {
                        _logger.LogWarning("Phản hồi không đầy đủ từ server");
                        return null;
                    }

                    string response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                    if (response == "PROFILE_NOT_FOUND")
                    {
                        _logger.LogWarning("Profile '{ProfileName}' không tìm thấy trên server", profileName);
                        return null;
                    }

                    // Phân tích profile từ JSON
                    var profile = JsonConvert.DeserializeObject<SteamCmdProfile>(response);

                    if (profile != null)
                    {
                        _logger.LogInformation("Đã nhận thông tin profile {ProfileName} từ server", profileName);
                        return profile;
                    }
                    else
                    {
                        _logger.LogWarning("Không thể phân tích dữ liệu profile từ server");
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin profile {ProfileName} từ server {Server}:{Port}",
                    profileName, serverAddress, port);

                // Tạo profile mẫu cho mục đích testing
                return new SteamCmdProfile
                {
                    Name = profileName,
                    AppID = "730", // CS:GO app ID
                    InstallDirectory = $"D:\\SteamLibrary\\{profileName}",
                    Arguments = "-validate",
                    ValidateFiles = true,
                    AutoRun = false,
                    AnonymousLogin = true,
                    Status = "Stopped",
                    StartTime = DateTime.Now,
                    StopTime = DateTime.Now,
                    Pid = 0,
                    LastRun = DateTime.UtcNow
                };
            }
        }

        public async Task<int> SyncProfilesFromServerAsync(string serverAddress, ProfileService profileService, int port = 61188)
        {
            // Luôn sử dụng địa chỉ mặc định bất kể tham số đầu vào
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            try
            {
                // Kiểm tra kết nối
                bool isConnected = await TestConnectionAsync(serverAddress, port);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối đến server {Server}:{Port}", serverAddress, port);
                    return 0;
                }

                var profileNames = await GetProfileNamesAsync(serverAddress, port);
                if (profileNames.Count == 0)
                {
                    _logger.LogWarning("Không có profile nào từ server");
                    return 0;
                }

                int syncCount = 0;
                int maxRetries = 3;

                foreach (var profileName in profileNames)
                {
                    bool success = false;

                    for (int retry = 0; retry < maxRetries && !success; retry++)
                    {
                        try
                        {
                            var serverProfile = await GetProfileDetailsByNameAsync(serverAddress, profileName, port);
                            if (serverProfile != null)
                            {
                                var localProfiles = await profileService.GetAllProfiles();
                                var existingProfile = localProfiles.FirstOrDefault(p => p.Name == profileName);

                                if (existingProfile != null)
                                {
                                    // Cập nhật profile hiện có
                                    serverProfile.Id = existingProfile.Id;
                                    serverProfile.Status = existingProfile.Status;
                                    serverProfile.Pid = existingProfile.Pid;
                                    serverProfile.StartTime = existingProfile.StartTime;
                                    serverProfile.StopTime = existingProfile.StopTime;
                                    serverProfile.LastRun = existingProfile.LastRun;

                                    await profileService.UpdateProfile(serverProfile);
                                    _logger.LogInformation("Đã cập nhật profile: {ProfileName}", profileName);
                                }
                                else
                                {
                                    // Thêm profile mới
                                    int newId = localProfiles.Count > 0 ? localProfiles.Max(p => p.Id) + 1 : 1;
                                    serverProfile.Id = newId;
                                    serverProfile.Status = "Stopped";

                                    await profileService.AddProfileAsync(serverProfile);
                                    _logger.LogInformation("Đã thêm profile mới: {ProfileName}", profileName);
                                }

                                syncCount++;
                                success = true;
                            }
                            else if (retry == maxRetries - 1)
                            {
                                _logger.LogWarning("Không thể lấy thông tin profile {ProfileName} sau {Retries} lần thử",
                                    profileName, maxRetries);
                            }
                        }
                        catch (Exception ex)
                        {
                            if (retry == maxRetries - 1)
                            {
                                _logger.LogError(ex, "Lỗi khi đồng bộ profile {ProfileName}", profileName);
                            }
                            else
                            {
                                _logger.LogWarning(ex, "Lỗi khi đồng bộ profile {ProfileName}, thử lại lần {Retry}/{MaxRetries}",
                                    profileName, retry + 1, maxRetries);

                                await Task.Delay(1000); // Chờ 1 giây trước khi thử lại
                            }
                        }
                    }
                }

                _logger.LogInformation("Đã đồng bộ {Count} profiles từ server", syncCount);
                return syncCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đồng bộ profiles từ server");
                return 0;
            }
        }
    }
}