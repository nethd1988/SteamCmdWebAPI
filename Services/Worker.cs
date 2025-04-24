
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly SteamCmdService _steamCmdService;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly ProfileService _profileService;

        public Worker(
            ILogger<Worker> logger,
            SteamCmdService steamCmdService,
            ServerSettingsService serverSettingsService,
            ProfileService profileService)
        {
            _logger = logger;
            _steamCmdService = steamCmdService;
            _serverSettingsService = serverSettingsService;
            _profileService = profileService;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Dịch vụ SteamCmdWebAPI đang khởi động...");

            // Đảm bảo thư mục data có đủ quyền
            try
            {
                var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string dataDir = Path.Combine(baseDirectory, "data");
                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                    _logger.LogInformation("Đã tạo thư mục data tại {0}", dataDir);
                }

                // Cấp quyền đầy đủ cho thư mục data
                SetFullPermissionsForEveryone(dataDir);
                _logger.LogInformation("Đã cấp quyền đầy đủ cho thư mục {0}", dataDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thiết lập quyền cho thư mục: {Message}", ex.Message);
            }

            await base.StartAsync(cancellationToken);
        }

        private void SetFullPermissionsForEveryone(string path)
        {
            try
            {
                DirectoryInfo dirInfo = new DirectoryInfo(path);
                DirectorySecurity dirSecurity = dirInfo.GetAccessControl();

                // Thêm quyền Full Control cho Everyone
                SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
                FileSystemAccessRule rule = new FileSystemAccessRule(
                    everyone,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow
                );

                dirSecurity.AddAccessRule(rule);
                dirInfo.SetAccessControl(dirSecurity);

                // Đặt quyền cho các thư mục con
                foreach (var subDir in Directory.GetDirectories(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        SetFullPermissionsForDirectory(subDir);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể đặt quyền cho thư mục {Dir}: {Message}", subDir, ex.Message);
                    }
                }

                // Đặt quyền cho các file
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        SetFullPermissionsForFile(file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể đặt quyền cho file {File}: {Message}", file, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đặt quyền cho thư mục {Path}: {Message}", path, ex.Message);
            }
        }

        private void SetFullPermissionsForDirectory(string dirPath)
        {
            DirectoryInfo dirInfo = new DirectoryInfo(dirPath);
            DirectorySecurity dirSecurity = dirInfo.GetAccessControl();

            SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            FileSystemAccessRule rule = new FileSystemAccessRule(
                everyone,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow
            );

            dirSecurity.AddAccessRule(rule);
            dirInfo.SetAccessControl(dirSecurity);
        }

        private void SetFullPermissionsForFile(string filePath)
        {
            FileInfo fileInfo = new FileInfo(filePath);
            FileSecurity fileSecurity = fileInfo.GetAccessControl();

            SecurityIdentifier everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            FileSystemAccessRule rule = new FileSystemAccessRule(
                everyone,
                FileSystemRights.FullControl,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow
            );

            fileSecurity.AddAccessRule(rule);
            fileInfo.SetAccessControl(fileSecurity);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                _logger.LogInformation("Dịch vụ SteamCmdWebAPI đã khởi động thành công");

                // Loại bỏ đoạn code AutoRun ở đây
                // AutoRun được xử lý bởi AutoRunBackgroundService
                //var settings = await _profileService.LoadAutoRunSettings();
                //if (settings.AutoRunEnabled)
                //{
                //    _logger.LogInformation("AutoRun được bật, đang khởi động các profile được đánh dấu...");
                //    await _steamCmdService.StartAllAutoRunProfilesAsync();
                //}

                // Dịch vụ Windows chạy liên tục
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình thực thi dịch vụ SteamCmdWebAPI");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Dịch vụ SteamCmdWebAPI đang dừng...");

            // Dừng tất cả các tiến trình đang chạy
            await _steamCmdService.StopAllProfilesAsync();

            await base.StopAsync(cancellationToken);
            _logger.LogInformation("Dịch vụ SteamCmdWebAPI đã dừng thành công");
        }
    }
}