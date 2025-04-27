using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;
using SteamCmdWebAPI.Services;
using System.Linq;

namespace SteamCmdWebAPI.Pages
{
    public class AutoRegisterModel : PageModel
    {
        private readonly ILogger<AutoRegisterModel> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly TcpClientService _tcpClientService;
        private readonly string _registrationFile;

        // Cài đặt cố định, không cho phép thay đổi
        private const string DEFAULT_SERVER_ADDRESS = "idckz.ddnsfree.com";
        private const int DEFAULT_SERVER_PORT = 61188;
        private const string AUTH_TOKEN = "simple_auth_token";

        public string ErrorMessage { get; set; }
        public string SuccessMessage { get; set; }

        public bool IsRegistered { get; set; }
        public string CurrentClientId { get; set; }
        public DateTime? RegisteredTime { get; set; }

        public AutoRegisterModel(ILogger<AutoRegisterModel> logger, IHttpClientFactory httpClientFactory, TcpClientService tcpClientService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _tcpClientService = tcpClientService;

            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }
            _registrationFile = Path.Combine(dataDir, "client_registration.json");
        }

        public void OnGet()
        {
            LoadRegistrationInfo();
        }

        public async Task<IActionResult> OnPostAutoRegisterAsync()
        {
            try
            {
                // Kiểm tra nếu đã đăng ký rồi
                LoadRegistrationInfo();
                if (IsRegistered)
                {
                    return new JsonResult(new { success = true, message = "Client đã được đăng ký từ trước" });
                }

                var myIpAddress = GetMyIpAddress();
                if (string.IsNullOrEmpty(myIpAddress))
                {
                    _logger.LogError("Không thể lấy địa chỉ IP của máy này");
                    return new JsonResult(new { success = false, error = "Không thể lấy địa chỉ IP của máy này" });
                }

                // Kiểm tra kết nối đến server trước
                if (!await TestServerConnection(DEFAULT_SERVER_ADDRESS, DEFAULT_SERVER_PORT))
                {
                    _logger.LogError("Không thể kết nối đến server {Server}:{Port}", DEFAULT_SERVER_ADDRESS, DEFAULT_SERVER_PORT);
                    return new JsonResult(new { success = false, error = "Không thể kết nối đến server" });
                }

                // Tạo ClientID
                string clientId = GetClientIdentifier();

                // Gửi request đăng ký
                var requestData = new
                {
                    ClientId = clientId,
                    Description = $"Client từ {Environment.MachineName} - {Environment.UserName}",
                    Address = myIpAddress,
                    Port = 61188,
                    AuthToken = AUTH_TOKEN,
                    OperatingSystem = Environment.OSVersion.ToString(),
                    Version = "1.0"
                };

                var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);

                var jsonContent = JsonSerializer.Serialize(requestData);
                var requestContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync($"http://{DEFAULT_SERVER_ADDRESS}:{DEFAULT_SERVER_PORT}/api/client/register", requestContent);

                if (response.IsSuccessStatusCode)
                {
                    // Lưu thông tin đăng ký
                    SaveRegistrationInfo(clientId);

                    _logger.LogInformation("Đăng ký thành công với server {Server}:{Port}", DEFAULT_SERVER_ADDRESS, DEFAULT_SERVER_PORT);
                    return new JsonResult(new { success = true, message = "Đăng ký thành công!" });
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogError("Đăng ký thất bại với server: {Error}", errorContent);
                    return new JsonResult(new { success = false, error = $"Đăng ký thất bại: {response.StatusCode}" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tự động đăng ký");
                return new JsonResult(new { success = false, error = ex.Message });
            }
        }

        public IActionResult OnGetUnregister()
        {
            try
            {
                if (System.IO.File.Exists(_registrationFile))
                {
                    System.IO.File.Delete(_registrationFile);
                    _logger.LogInformation("Đã hủy đăng ký client");
                    SuccessMessage = "Đã hủy đăng ký thành công!";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi hủy đăng ký");
                ErrorMessage = $"Lỗi khi hủy đăng ký: {ex.Message}";
            }

            LoadRegistrationInfo();
            return Page();
        }

        public IActionResult OnGetMyIp()
        {
            var ip = GetMyIpAddress();
            return new JsonResult(new { ip = ip });
        }

        private string GetClientIdentifier()
        {
            string machineName = Environment.MachineName;
            string userName = Environment.UserName;
            string clientId = $"{machineName}-{userName}-{DateTime.Now:yyyyMMdd}";
            return clientId;
        }

        private string GetMyIpAddress()
        {
            try
            {
                // Lấy IP cục bộ
                foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (networkInterface.OperationalStatus == OperationalStatus.Up &&
                        networkInterface.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    {
                        IPInterfaceProperties ipProperties = networkInterface.GetIPProperties();
                        foreach (UnicastIPAddressInformation ip in ipProperties.UnicastAddresses)
                        {
                            if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                return ip.Address.ToString();
                            }
                        }
                    }
                }
                return "127.0.0.1";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy địa chỉ IP");
                return null;
            }
        }

        private async Task<bool> TestServerConnection(string address, int port)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var result = client.BeginConnect(address, port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(3));

                if (success)
                {
                    client.EndConnect(result);
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private void SaveRegistrationInfo(string clientId)
        {
            try
            {
                var registrationInfo = new
                {
                    ClientId = clientId,
                    RegisteredTime = DateTime.Now
                };

                var json = JsonSerializer.Serialize(registrationInfo, new JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(_registrationFile, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu thông tin đăng ký");
            }
        }

        private void LoadRegistrationInfo()
        {
            try
            {
                if (System.IO.File.Exists(_registrationFile))
                {
                    var json = System.IO.File.ReadAllText(_registrationFile);
                    var registrationInfo = JsonSerializer.Deserialize<RegistrationInfo>(json);

                    if (registrationInfo != null)
                    {
                        IsRegistered = true;
                        CurrentClientId = registrationInfo.ClientId;
                        RegisteredTime = registrationInfo.RegisteredTime;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải thông tin đăng ký");
            }
        }

        private class RegistrationInfo
        {
            public string ClientId { get; set; }
            public DateTime RegisteredTime { get; set; }
        }
    }
}