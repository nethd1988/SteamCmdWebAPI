using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class TcpClientService
    {
        private readonly ILogger<TcpClientService> _logger;
        private readonly EncryptionService _encryptionService;
        private readonly LicenseService _licenseService;
        private readonly string _clientId;

        // Địa chỉ server mặc định
        private const string DEFAULT_SERVER_ADDRESS = "idckz.ddnsfree.com";
        private const int DEFAULT_SERVER_PORT = 61188;
        private const string AUTH_TOKEN = "simple_auth_token";
        private const int DEFAULT_TIMEOUT_MS = 10000; // 10 giây

        public TcpClientService(ILogger<TcpClientService> logger, EncryptionService encryptionService, LicenseService licenseService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
            _clientId = GetClientIdentifier();
            _logger.LogInformation("TcpClientService initialized with ClientID: {ClientId}", _clientId);
        }

        private string GetClientIdentifier()
        {
            try
            {
                // Gọi LicenseService để lấy username trực tiếp
                string username = _licenseService.GetLicenseUsername();

                if (!string.IsNullOrEmpty(username))
                {
                    _logger.LogInformation("Got username from license: {Username}", username);
                    return username;
                }

                _logger.LogWarning("Không lấy được username từ license, sử dụng client ID mặc định");
                return GenerateDefaultClientId();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy username từ license");
                return GenerateDefaultClientId();
            }
        }

        private string GenerateDefaultClientId()
        {
            string machineName = Environment.MachineName;
            string userName = Environment.UserName;
            string clientId = $"{machineName}-{userName}-{DateTime.Now:yyyyMMdd}";
            return clientId;
        }

        public async Task<bool> TestConnectionAsync(string serverAddress, int port = DEFAULT_SERVER_PORT)
        {
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            try
            {
                _logger.LogInformation("Kiểm tra kết nối đến {ServerAddress}:{Port}", serverAddress, port);

                using (var tcpClient = new TcpClient())
                {
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    var timeoutTask = Task.Delay(5000);

                    if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                    {
                        _logger.LogWarning("Kết nối đến {ServerAddress}:{Port} bị timeout", serverAddress, port);
                        return false;
                    }

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
                return false;
            }
        }

        public async Task PeriodicHeartbeatAsync(CancellationToken cancellationToken = default)
        {
            string serverAddress = DEFAULT_SERVER_ADDRESS;
            int port = DEFAULT_SERVER_PORT;

            try
            {
                using (var tcpClient = new TcpClient())
                {
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(5000, cancellationToken)) != connectTask)
                    {
                        _logger.LogWarning("Kết nối đến server {Server}:{Port} bị timeout", serverAddress, port);
                        return;
                    }

                    if (!tcpClient.Connected)
                    {
                        _logger.LogWarning("Không thể kết nối đến server {Server}:{Port}", serverAddress, port);
                        return;
                    }

                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH + CLIENT_ID + HEARTBEAT
                    string command = $"AUTH:{AUTH_TOKEN} CLIENT_ID:{_clientId} HEARTBEAT";
                    byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                    byte[] lengthBytes = BitConverter.GetBytes(commandBytes.Length);

                    await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length, cancellationToken);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length, cancellationToken);
                    await stream.FlushAsync(cancellationToken);

                    // Đọc phản hồi
                    byte[] responseHeaderBuffer = new byte[4];
                    int bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4, cancellationToken);

                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi từ server");
                        return;
                    }

                    int responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    if (responseLength <= 0 || responseLength > 1024 * 1024)
                    {
                        _logger.LogWarning("Độ dài phản hồi không hợp lệ: {Length}", responseLength);
                        return;
                    }

                    byte[] responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength, cancellationToken);

                    if (bytesRead < responseLength)
                    {
                        _logger.LogWarning("Phản hồi không đầy đủ từ server");
                        return;
                    }

                    string response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);
                    _logger.LogDebug("Nhận phản hồi từ server: {Response}", response);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi heartbeat đến server {Server}:{Port}", serverAddress, port);
            }
        }

        public async Task<List<string>> GetProfileNamesAsync(string serverAddress, int port = DEFAULT_SERVER_PORT)
        {
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            try
            {
                bool isConnected = await TestConnectionAsync(serverAddress, port);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối đến server {Server}:{Port}", serverAddress, port);
                    return new List<string>();
                }

                _logger.LogInformation("Đang lấy danh sách profile từ {Server}:{Port}", serverAddress, port);

                using (var tcpClient = new TcpClient())
                {
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    var timeoutTask = Task.Delay(DEFAULT_TIMEOUT_MS);

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

                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH + CLIENT_ID + GET_PROFILES
                    string command = $"AUTH:{AUTH_TOKEN} CLIENT_ID:{_clientId} GET_PROFILES";
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
                    if (responseLength <= 0 || responseLength > 1024 * 1024)
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

                    var profileNames = new List<string>(response.Split(',', StringSplitOptions.RemoveEmptyEntries));
                    _logger.LogInformation("Đã nhận {Count} profiles từ server", profileNames.Count);
                    return profileNames;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profile từ server {Server}:{Port}", serverAddress, port);
                return new List<string>();
            }
        }

        public async Task<SteamCmdProfile> GetProfileDetailsByNameAsync(string serverAddress, string profileName, int port = DEFAULT_SERVER_PORT)
        {
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            if (string.IsNullOrEmpty(profileName))
            {
                _logger.LogWarning("Tên profile không được để trống");
                return null;
            }

            try
            {
                _logger.LogInformation("Lấy thông tin profile {ProfileName} từ server {Server}:{Port}", profileName, serverAddress, port);

                bool isConnected = await TestConnectionAsync(serverAddress, port);
                if (!isConnected)
                {
                    _logger.LogWarning("Không thể kết nối đến server {Server}:{Port}", serverAddress, port);
                    return null;
                }

                using (var tcpClient = new TcpClient())
                {
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    var timeoutTask = Task.Delay(DEFAULT_TIMEOUT_MS);

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

                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh
                    string command = $"AUTH:{AUTH_TOKEN} CLIENT_ID:{_clientId} GET_PROFILE_DETAILS {profileName}";
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
                    if (responseLength <= 0 || responseLength > 5 * 1024 * 1024)
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

                    if (response.StartsWith("ERROR:"))
                    {
                        _logger.LogWarning("Server báo lỗi: {Error}", response);
                        return null;
                    }

                    try
                    {
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var profile = JsonSerializer.Deserialize<SteamCmdProfile>(response, options);

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
                    catch (JsonException jsonEx)
                    {
                        _logger.LogError(jsonEx, "Lỗi khi phân tích JSON từ server: {Response}", response);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin profile {ProfileName} từ server", profileName);
                return null;
            }
        }

        public async Task<bool> SendProfileToServerAsync(SteamCmdProfile profile)
        {
            string serverAddress = DEFAULT_SERVER_ADDRESS;
            int port = DEFAULT_SERVER_PORT;

            try
            {
                if (profile == null)
                {
                    _logger.LogWarning("Không có profile để gửi lên server");
                    return false;
                }

                _logger.LogInformation("Đang gửi profile {ProfileName} lên server {Server}:{Port}", profile.Name, serverAddress, port);

                using (var tcpClient = new TcpClient())
                {
                    // Kết nối với timeout
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(DEFAULT_TIMEOUT_MS)) != connectTask)
                    {
                        _logger.LogWarning("Kết nối đến {Server}:{Port} bị timeout", serverAddress, port);
                        return false;
                    }

                    if (!tcpClient.Connected)
                    {
                        _logger.LogWarning("Không thể kết nối đến {Server}:{Port}", serverAddress, port);
                        return false;
                    }

                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH và SEND_PROFILE
                    string command = $"AUTH:{AUTH_TOKEN} CLIENT_ID:{_clientId} SEND_PROFILE";
                    byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                    byte[] lengthBytes = BitConverter.GetBytes(commandBytes.Length);

                    await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi từ server
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

                    // Chuyển profile thành JSON và gửi
                    string json = JsonSerializer.Serialize(profile);
                    byte[] profileBytes = Encoding.UTF8.GetBytes(json);

                    // Gửi độ dài trước
                    byte[] profileLengthBytes = BitConverter.GetBytes(profileBytes.Length);
                    await stream.WriteAsync(profileLengthBytes, 0, profileLengthBytes.Length);

                    // Gửi dữ liệu
                    await stream.WriteAsync(profileBytes, 0, profileBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi kết quả
                    bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);
                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi kết quả");
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
                    else
                    {
                        _logger.LogWarning("Lỗi khi gửi profile {ProfileName} lên server: {Error}", profile.Name, response);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi profile {ProfileName} lên server", profile.Name);
                return false;
            }
        }

        public async Task<bool> SendMultipleProfilesToServerAsync(List<SteamCmdProfile> profiles)
        {
            string serverAddress = DEFAULT_SERVER_ADDRESS;
            int port = DEFAULT_SERVER_PORT;

            try
            {
                if (profiles == null || profiles.Count == 0)
                {
                    _logger.LogWarning("Không có profiles để gửi lên server");
                    return false;
                }

                _logger.LogInformation("Đang gửi {Count} profiles lên server {Server}:{Port}", profiles.Count, serverAddress, port);

                using (var tcpClient = new TcpClient())
                {
                    // Kết nối với timeout
                    var connectTask = tcpClient.ConnectAsync(serverAddress, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(DEFAULT_TIMEOUT_MS)) != connectTask)
                    {
                        _logger.LogWarning("Kết nối đến {Server}:{Port} bị timeout", serverAddress, port);
                        return false;
                    }

                    if (!tcpClient.Connected)
                    {
                        _logger.LogWarning("Không thể kết nối đến {Server}:{Port}", serverAddress, port);
                        return false;
                    }

                    using var stream = tcpClient.GetStream();

                    // Gửi lệnh AUTH và SEND_PROFILES
                    string command = $"AUTH:{AUTH_TOKEN} CLIENT_ID:{_clientId} SEND_PROFILES";
                    byte[] commandBytes = Encoding.UTF8.GetBytes(command);
                    byte[] lengthBytes = BitConverter.GetBytes(commandBytes.Length);

                    await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                    await stream.WriteAsync(commandBytes, 0, commandBytes.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi từ server
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
                        _logger.LogWarning("Server không sẵn sàng nhận profiles: {Response}", response);
                        return false;
                    }

                    // Gửi từng profile một
                    int sentCount = 0;
                    foreach (var profile in profiles)
                    {
                        string json = JsonSerializer.Serialize(profile);
                        byte[] profileBytes = Encoding.UTF8.GetBytes(json);

                        // Gửi độ dài
                        byte[] profileLengthBytes = BitConverter.GetBytes(profileBytes.Length);
                        await stream.WriteAsync(profileLengthBytes, 0, profileLengthBytes.Length);

                        // Gửi dữ liệu
                        await stream.WriteAsync(profileBytes, 0, profileBytes.Length);
                        await stream.FlushAsync();

                        sentCount++;
                    }

                    // Gửi marker kết thúc (độ dài 0)
                    byte[] endMarker = BitConverter.GetBytes(0);
                    await stream.WriteAsync(endMarker, 0, endMarker.Length);
                    await stream.FlushAsync();

                    // Đọc phản hồi kết quả
                    bytesRead = await stream.ReadAsync(responseHeaderBuffer, 0, 4);
                    if (bytesRead < 4)
                    {
                        _logger.LogWarning("Không đọc được phản hồi kết quả");
                        return false;
                    }

                    responseLength = BitConverter.ToInt32(responseHeaderBuffer, 0);
                    responseBuffer = new byte[responseLength];
                    bytesRead = await stream.ReadAsync(responseBuffer, 0, responseLength);

                    response = Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);

                    if (response.StartsWith("DONE:"))
                    {
                        string[] parts = response.Split(':');
                        if (parts.Length >= 3)
                        {
                            int processedCount = int.Parse(parts[1]);
                            int errorCount = int.Parse(parts[2]);
                            _logger.LogInformation("Đã gửi {Total} profiles, xử lý thành công: {Success}, lỗi: {Error}",
                                profiles.Count, processedCount, errorCount);
                            return processedCount > 0;
                        }
                        return true;
                    }
                    else
                    {
                        _logger.LogWarning("Lỗi khi gửi profiles lên server: {Response}", response);
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi profiles lên server");
                return false;
            }
        }
    }
}