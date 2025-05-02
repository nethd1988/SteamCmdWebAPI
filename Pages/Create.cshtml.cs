using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI.Pages
{
    public class ScanGameResult
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public string InstallPath { get; set; }
    }

    public class CreateModel : PageModel
    {
        private readonly ProfileService _profileService;
        private readonly EncryptionService _encryptionService;
        private readonly ILogger<CreateModel> _logger;
        private readonly TcpClientService _tcpClientService;
        private readonly ServerSettingsService _serverSettingsService;
        private readonly SteamAccountService _steamAccountService;

        [BindProperty]
        public SteamCmdProfile Profile { get; set; } = new SteamCmdProfile();

        [BindProperty]
        public string Username { get; set; } = string.Empty;

        [BindProperty]
        public string Password { get; set; } = string.Empty;

        [BindProperty]
        public bool UseSteamAccounts { get; set; } = false;

        [TempData]
        public string StatusMessage { get; set; }

        [TempData]
        public bool IsSuccess { get; set; }

        public List<string> ServerProfiles { get; set; } = new List<string>();

        public CreateModel(
            ProfileService profileService,
            EncryptionService encryptionService,
            ILogger<CreateModel> logger,
            TcpClientService tcpClientService,
            ServerSettingsService serverSettingsService,
            SteamAccountService steamAccountService)
        {
            _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tcpClientService = tcpClientService ?? throw new ArgumentNullException(nameof(tcpClientService));
            _serverSettingsService = serverSettingsService ?? throw new ArgumentNullException(nameof(serverSettingsService));
            _steamAccountService = steamAccountService ?? throw new ArgumentNullException(nameof(steamAccountService));
        }

        public async Task<IActionResult> OnGetAsync()
        {
            // Xóa StatusMessage để tránh hiển thị thông báo lỗi cũ
            StatusMessage = null;
            IsSuccess = false;

            // Lấy danh sách profile từ server
            try
            {
                ServerProfiles = await _tcpClientService.GetProfileNamesAsync("", 0);
                _logger.LogInformation("Đã lấy {Count} profiles từ server", ServerProfiles.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy danh sách profiles từ server");
                ServerProfiles = new List<string>();
            }

            return Page();
        }

        // Handler để lấy profile từ server
        public async Task<IActionResult> OnGetProfileFromServerAsync(string profileName)
        {
            if (string.IsNullOrEmpty(profileName))
            {
                return BadRequest(new { success = false, error = "Tên profile không được để trống" });
            }

            try
            {
                var profile = await _tcpClientService.GetProfileDetailsByNameAsync("", profileName, 0);
                if (profile != null)
                {
                    return new JsonResult(new { success = true, profile = profile });
                }
                else
                {
                    return NotFound(new { success = false, error = "Không tìm thấy profile" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy profile {ProfileName} từ server", profileName);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        public async Task<IActionResult> OnPostSaveProfileAsync()
        {
            _logger.LogInformation("OnPostSaveProfileAsync called");

            // Loại bỏ các trường không cần thiết khỏi ModelState
            ModelState.Remove("Profile.Arguments");

            // Xóa thông báo lỗi cũ
            StatusMessage = null;

            // Kiểm tra UseSteamAccounts và bỏ qua validation cho Username và Password nếu UseSteamAccounts = true
            if (UseSteamAccounts)
            {
                ModelState.Remove("Username");
                ModelState.Remove("Password");
                _logger.LogInformation("Người dùng đã chọn sử dụng tài khoản từ SteamAccounts, bỏ qua kiểm tra Username/Password");
            }

            // Kiểm tra validation
            if (!ModelState.IsValid)
            {
                var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToList();
                _logger.LogWarning("Validation failed with errors: {Errors}", string.Join(", ", errors));
                StatusMessage = "Vui lòng kiểm tra lại thông tin: " + string.Join(", ", errors);
                IsSuccess = false;

                // Lấy lại danh sách profile từ server
                try
                {
                    ServerProfiles = await _tcpClientService.GetProfileNamesAsync("", 0);
                }
                catch
                {
                    ServerProfiles = new List<string>();
                }

                return Page();
            }

            try
            {
                _logger.LogInformation("Bắt đầu lưu profile: {Name}, AppID: {AppID}, InstallDirectory: {InstallDirectory}",
                    Profile.Name, Profile.AppID, Profile.InstallDirectory);

                // Kiểm tra xem người dùng có chọn sử dụng tài khoản từ SteamAccounts hay không
                if (UseSteamAccounts)
                {
                    _logger.LogInformation("Người dùng đã chọn sử dụng tài khoản từ SteamAccounts");
                    // Bỏ qua thông tin đăng nhập
                    Profile.SteamUsername = string.Empty;
                    Profile.SteamPassword = string.Empty;
                }
                else if (!string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password))
                {
                    // Mã hóa thông tin đăng nhập Steam nếu có
                    if (!string.IsNullOrEmpty(Username))
                    {
                        try
                        {
                            Profile.SteamUsername = _encryptionService.Encrypt(Username);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi mã hóa tên đăng nhập");
                            StatusMessage = "Lỗi khi mã hóa thông tin đăng nhập: " + ex.Message;
                            IsSuccess = false;
                            return Page();
                        }
                    }

                    if (!string.IsNullOrEmpty(Password))
                    {
                        try
                        {
                            Profile.SteamPassword = _encryptionService.Encrypt(Password);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi mã hóa mật khẩu");
                            StatusMessage = "Lỗi khi mã hóa thông tin đăng nhập: " + ex.Message;
                            IsSuccess = false;
                            return Page();
                        }
                    }
                    
                    // Nếu có thông tin tài khoản, lưu vào SteamAccounts
                    if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
                    {
                        try 
                        {
                            // Kiểm tra xem tài khoản đã tồn tại chưa
                            var existingAccounts = await _steamAccountService.GetAllAccountsAsync();
                            var existingAccount = existingAccounts.FirstOrDefault(a => 
                                _encryptionService.Decrypt(a.Username).Equals(Username, StringComparison.OrdinalIgnoreCase));
                            
                            if (existingAccount == null)
                            {
                                // Tạo tài khoản mới trong SteamAccounts
                                var steamAccount = new SteamAccount
                                {
                                    ProfileName = Profile.Name,
                                    Username = _encryptionService.Encrypt(Username),
                                    Password = _encryptionService.Encrypt(Password),
                                    AppIds = Profile.AppID,
                                    GameNames = Profile.Name,
                                    CreatedAt = DateTime.Now,
                                    UpdatedAt = DateTime.Now
                                };
                                
                                await _steamAccountService.AddAccountAsync(steamAccount);
                                _logger.LogInformation("Đã thêm tài khoản {Username} vào SteamAccounts", Username);
                            }
                            else
                            {
                                // Cập nhật thêm AppID vào tài khoản hiện có
                                var appIds = existingAccount.AppIds?.Split(',').ToList() ?? new List<string>();
                                if (!appIds.Contains(Profile.AppID))
                                {
                                    appIds.Add(Profile.AppID);
                                    existingAccount.AppIds = string.Join(",", appIds);
                                    existingAccount.UpdatedAt = DateTime.Now;
                                    
                                    // Cập nhật tên game
                                    var gameNames = existingAccount.GameNames?.Split(',').ToList() ?? new List<string>();
                                    if (!gameNames.Contains(Profile.Name))
                                    {
                                        gameNames.Add(Profile.Name);
                                        existingAccount.GameNames = string.Join(",", gameNames);
                                    }
                                    
                                    await _steamAccountService.UpdateAccountAsync(existingAccount);
                                    _logger.LogInformation("Đã cập nhật AppID {AppID} vào tài khoản {Username} trong SteamAccounts", Profile.AppID, Username);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi thêm tài khoản vào SteamAccounts");
                            // Tiếp tục tạo profile ngay cả khi không thêm được vào SteamAccounts
                        }
                    }
                }

                if (string.IsNullOrEmpty(Profile.Arguments))
                {
                    Profile.Arguments = string.Empty;
                }

                // Đặt trạng thái ban đầu
                Profile.Status = "Stopped";
                Profile.StartTime = DateTime.Now;
                Profile.StopTime = DateTime.Now;
                Profile.LastRun = DateTime.UtcNow;
                Profile.Pid = 0;

                await _profileService.AddProfileAsync(Profile);

                _logger.LogInformation("Đã lưu profile thành công: {Name}", Profile.Name);
                TempData["Success"] = $"Đã thêm mới cấu hình {Profile.Name}";

                // Gửi profile về server
                var serverSettings = await _serverSettingsService.LoadSettingsAsync();
                if (serverSettings.EnableServerSync)
                {
                    await _tcpClientService.SendProfileToServerAsync(Profile);
                    _logger.LogInformation("Đã gửi profile {Name} về server", Profile.Name);
                }

                return RedirectToPage("/UpdateManagement");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu profile trong Create");
                StatusMessage = $"Đã xảy ra lỗi khi lưu profile: {ex.Message}";
                IsSuccess = false;
                
                // Lấy lại danh sách profile từ server
                try
                {
                    ServerProfiles = await _tcpClientService.GetProfileNamesAsync("", 0);
                }
                catch
                {
                    ServerProfiles = new List<string>();
                }

                return Page();
            }
        }

        // Handler để nhập profile từ server
        public async Task<IActionResult> OnPostImportFromServerAsync(string profileName)
        {
            try
            {
                if (string.IsNullOrEmpty(profileName))
                {
                    return BadRequest(new { success = false, error = "Tên profile không được để trống" });
                }

                var serverProfile = await _tcpClientService.GetProfileDetailsByNameAsync("", profileName, 0);
                if (serverProfile == null)
                {
                    return NotFound(new { success = false, error = $"Không tìm thấy profile '{profileName}' trên server" });
                }

                // Kiểm tra xem profile đã tồn tại chưa
                var existingProfile = (await _profileService.GetAllProfiles())
                    .FirstOrDefault(p => p.Name == serverProfile.Name);

                if (existingProfile != null)
                {
                    return BadRequest(new { success = false, error = $"Profile '{profileName}' đã tồn tại trong hệ thống" });
                }

                // Thêm profile mới
                await _profileService.AddProfileAsync(serverProfile);

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã nhập profile '{profileName}' từ server thành công",
                    redirectUrl = "/Dashboard"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi nhập profile {ProfileName} từ server", profileName);
                return StatusCode(500, new { success = false, error = ex.Message });
            }
        }

        // Handler để scan games từ thư mục
        public async Task<IActionResult> OnGetScanGamesAsync()
        {
            try
            {
                var scannedGames = new List<object>();
                var existingProfiles = await _profileService.GetAllProfiles();
                var existingAppIds = existingProfiles.Select(p => p.AppID).ToList();
                var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Quét tất cả ổ đĩa cố định
                var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady);

                foreach (var drive in drives)
                {
                    // Đường dẫn Steam cơ bản cần kiểm tra
                    var steamPaths = new[]
                    {
                        Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam"),
                        Path.Combine(drive.RootDirectory.FullName, "Program Files", "Steam"),
                        Path.Combine(drive.RootDirectory.FullName, "Steam"),
                        Path.Combine(drive.RootDirectory.FullName, "Games", "Steam"),
                        Path.Combine(drive.RootDirectory.FullName, "Games"),
                        Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"),
                        Path.Combine(drive.RootDirectory.FullName, "Online Games"),
                    };

                    foreach (var steamPath in steamPaths)
                    {
                        if (Directory.Exists(steamPath))
                        {
                            await ScanDirectoryForGamesAsync(steamPath, scannedGames, existingAppIds, processedPaths);
                        }
                    }

                    // Quét thư mục gốc của ổ đĩa (nếu là ổ SSD nhỏ)
                    const long maxSizeToScanInBytes = 240L * 1024 * 1024 * 1024; // 240GB
                    try
                    {
                        if (drive.TotalSize < maxSizeToScanInBytes)
                        {
                            await ScanDirectoryForGamesAsync(drive.RootDirectory.FullName, scannedGames, existingAppIds, processedPaths, 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể quét thư mục gốc của ổ đĩa {DriveName}", drive.Name);
                        // Tiếp tục quét các ổ đĩa khác
                    }
                }

                if (scannedGames.Count == 0)
                {
                    return new JsonResult(new { success = false, message = "Không tìm thấy game nào hoặc tất cả game đã có profile" });
                }

                return new JsonResult(new { success = true, games = scannedGames });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi scan games");
                return new JsonResult(new { success = false, message = ex.Message });
            }
        }

        // Thêm phương thức quét đệ quy
        private async Task ScanDirectoryForGamesAsync(string directory, List<object> scannedGames, List<string> existingAppIds, HashSet<string> processedPaths, int maxDepth = 2, int currentDepth = 0)
        {
            if (currentDepth > maxDepth || !Directory.Exists(directory) || processedPaths.Contains(directory))
                return;

            processedPaths.Add(directory);

            try
            {
                // Kiểm tra thư mục steamapps trong thư mục hiện tại
                var steamappsDir = Path.Combine(directory, "steamapps");
                if (Directory.Exists(steamappsDir))
                {
                    // Tìm tất cả file appmanifest_*.acf
                    var manifestFiles = Directory.GetFiles(steamappsDir, "appmanifest_*.acf");

                    foreach (var manifestFile in manifestFiles)
                    {
                        var match = Regex.Match(Path.GetFileName(manifestFile), @"appmanifest_(\d+)\.acf");
                        if (match.Success)
                        {
                            string appId = match.Groups[1].Value;

                            // Bỏ qua nếu đã có profile cho game này
                            if (existingAppIds.Contains(appId))
                                continue;

                            // Đọc file manifest
                            string content = await System.IO.File.ReadAllTextAsync(manifestFile);

                            // Lấy tên game từ manifest  
                            var nameMatch = Regex.Match(content, @"""name""\s+""([^""]+)""");
                            var gameName = nameMatch.Success ? nameMatch.Groups[1].Value : $"AppID {appId}";

                            // Bỏ qua Steamworks Common Redistributables và các gói phân phối lại
                            if (gameName.Contains("Steamworks Common Redistributables") ||
                                appId == "228980" ||
                                gameName.Contains("Redistributable") ||
                                gameName.Contains("Redist"))
                            {
                                continue;
                            }

                            // Lấy đường dẫn cài đặt (phụ huynh của steamapps)
                            var installDir = directory;

                            scannedGames.Add(new
                            {
                                appId,
                                gameName,
                                installDir
                            });
                        }
                    }
                }

                // Quét các thư mục con nếu chưa đạt độ sâu tối đa
                if (currentDepth < maxDepth)
                {
                    var subDirectories = Directory.GetDirectories(directory);
                    foreach (var subDir in subDirectories)
                    {
                        try
                        {
                            // Bỏ qua một số thư mục hệ thống để tăng tốc độ quét
                            var dirName = Path.GetFileName(subDir).ToLower();
                            if (dirName == "windows" || dirName == "program files" ||
                                dirName == "program files (x86)" || dirName == "$recycle.bin" ||
                                dirName == "system volume information" || dirName.StartsWith("$"))
                                continue;

                            await ScanDirectoryForGamesAsync(subDir, scannedGames, existingAppIds, processedPaths, maxDepth, currentDepth + 1);
                        }
                        catch
                        {
                            // Bỏ qua lỗi truy cập thư mục con
                        }
                    }
                }
            }
            catch
            {
                // Bỏ qua lỗi truy cập thư mục
            }
        }

        // Handler để nhập tất cả game vào trang quản lý cập nhật
        public async Task<IActionResult> OnPostImportAllGamesAsync([FromBody] List<ScanGameResult> games)
        {
            if (games == null || !games.Any())
            {
                return new JsonResult(new { success = false, message = "Không có game nào để nhập" });
            }

            try
            {
                int importedCount = 0;
                foreach (var game in games)
                {
                    if (string.IsNullOrEmpty(game.AppId) || string.IsNullOrEmpty(game.InstallPath))
                    {
                        continue;
                    }

                    var profile = new SteamCmdProfile
                    {
                        Name = game.Name,
                        AppID = game.AppId,
                        InstallDirectory = game.InstallPath,
                        Status = "Stopped",
                        StartTime = DateTime.Now,
                        StopTime = DateTime.Now,
                        LastRun = DateTime.UtcNow,
                        Pid = 0,
                        Arguments = string.Empty,
                        // Để trống các trường đăng nhập để sử dụng tài khoản từ SteamAccounts
                        SteamUsername = string.Empty,
                        SteamPassword = string.Empty,
                        ValidateFiles = true,
                        AutoRun = false
                    };

                    await _profileService.AddProfileAsync(profile);
                    importedCount++;
                }

                return new JsonResult(new { success = true, importedCount, message = $"Đã nhập thành công {importedCount} game" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi nhập tất cả game");
                return new JsonResult(new { success = false, message = $"Lỗi: {ex.Message}" });
            }
        }
    }
}