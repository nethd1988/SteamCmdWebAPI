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
    /// <summary>
    /// Dịch vụ TCP Client
    /// </summary>
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

        /// <summary>
        /// Kiểm tra kết nối tới server
        /// </summary>
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

        /// <summary>
        /// Lấy danh sách profile từ server (mô phỏng)
        /// </summary>
        public Task<List<string>> GetProfileNamesAsync(string serverAddress, int port = 61188)
        {
            // Luôn sử dụng địa chỉ mặc định bất kể tham số đầu vào
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            // Mô phỏng dữ liệu phản hồi
            var profileNames = new List<string>
            {
                "CS2 Server",
                "Minecraft Server",
                "ARK Survival",
                "Valheim Dedicated",
                "PUBG Test Server"
            };

            _logger.LogInformation("Giả lập lấy {Count} profiles từ server {Server}:{Port}", 
                profileNames.Count, serverAddress, port);
            
            return Task.FromResult(profileNames);
        }

        /// <summary>
        /// Lấy thông tin chi tiết profile từ server theo tên (mô phỏng)
        /// </summary>
        public Task<SteamCmdProfile> GetProfileDetailsByNameAsync(string serverAddress, string profileName, int port = 61188)
        {
            // Luôn sử dụng địa chỉ mặc định bất kể tham số đầu vào
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            if (string.IsNullOrEmpty(profileName))
            {
                _logger.LogWarning("Tên profile không được để trống");
                return Task.FromResult<SteamCmdProfile>(null);
            }

            // Tạo dữ liệu mẫu cho profile
            var profile = new SteamCmdProfile
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

            _logger.LogInformation("Giả lập lấy thông tin chi tiết cho profile {ProfileName} từ {Server}:{Port}",
                profileName, serverAddress, port);

            return Task.FromResult(profile);
        }

        /// <summary>
        /// Đồng bộ hóa profiles từ server (mô phỏng)
        /// </summary>
        public async Task<int> SyncProfilesFromServerAsync(string serverAddress, ProfileService profileService, int port = 61188)
        {
            // Luôn sử dụng địa chỉ mặc định bất kể tham số đầu vào
            serverAddress = DEFAULT_SERVER_ADDRESS;
            port = DEFAULT_SERVER_PORT;

            try
            {
                var profileNames = await GetProfileNamesAsync(serverAddress, port);
                int syncCount = 0;
                
                foreach (var profileName in profileNames)
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
                        }
                        else
                        {
                            // Thêm profile mới
                            int newId = localProfiles.Count > 0 ? localProfiles.Max(p => p.Id) + 1 : 1;
                            serverProfile.Id = newId;
                            serverProfile.Status = "Stopped";
                            
                            await profileService.AddProfileAsync(serverProfile);
                        }
                        
                        syncCount++;
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