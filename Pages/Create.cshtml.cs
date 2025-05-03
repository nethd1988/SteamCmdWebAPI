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
using System.Text.Json;

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

        [BindProperty]
        public string ServerProfileJson { get; set; } = string.Empty;

        // Thêm biến để lưu trạng thái profile lấy từ server
        private bool _isProfileFromServer = false;
        private SteamCmdProfile _serverProfileCache = null;

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

        // Xử lý API get thông tin từ server
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
                    _isProfileFromServer = true;
                    _serverProfileCache = profile;

                    // Đảm bảo xử lý đúng tên profile dài có dấu ":" 
                    if (profile.Name.Contains(":"))
                    {
                        _logger.LogInformation("Nhận được profile có tên dài từ server: {Name}", profile.Name);
                        // Đảm bảo hiển thị đầy đủ tên không bị cắt
                        profile.Name = profile.Name.Trim();
                    }

                    // Ghi log chi tiết thông tin nhận được từ server
                    _logger.LogInformation("Đã nhận profile từ server: Name={Name}, AppID={AppID}, Username={Username}, HasPassword={HasPassword}",
                        profile.Name, profile.AppID,
                        !string.IsNullOrEmpty(profile.SteamUsername) ? "[Có]" : "[Không]",
                        !string.IsNullOrEmpty(profile.SteamPassword) ? "[Có]" : "[Không]");

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

        // Handler lấy thông tin từ server - sửa tên phù hợp với client
        public async Task<IActionResult> OnGetProfileDetailsAsync(string profileName)
        {
            return await OnGetProfileFromServerAsync(profileName);
        }

        public async Task<IActionResult> OnPostSaveProfileAsync()
        {
            _logger.LogInformation("OnPostSaveProfileAsync called");
            // Loại bỏ validation cho ServerProfileJson
            ModelState.Remove("ServerProfileJson");

            // Nếu có profile từ server, loại bỏ validation cho Username/Password
            if (!string.IsNullOrEmpty(ServerProfileJson))
            {
                ModelState.Remove("Username");
                ModelState.Remove("Password");
            }
            // Luôn loại bỏ validation cho Profile.Arguments (không bắt buộc)
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

                // Kiểm tra trùng lặp AppID trước khi lưu
                if (!string.IsNullOrEmpty(Profile.AppID))
                {
                    var existingProfiles = await _profileService.GetAllProfiles();
                    var appIds = Profile.AppID.Split(',').Select(id => id.Trim()).ToList();

                    foreach (var appId in appIds)
                    {
                        var duplicateProfile = existingProfiles.FirstOrDefault(p =>
                            !string.IsNullOrEmpty(p.AppID) &&
                            p.AppID.Split(',').Select(id => id.Trim()).Contains(appId));

                        if (duplicateProfile != null)
                        {
                            StatusMessage = $"Lỗi: AppID {appId} đã tồn tại trong profile '{duplicateProfile.Name}'. Không thể tạo profile trùng lặp.";
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
                }

                // Đọc profile từ hidden field nếu có
                if (!string.IsNullOrEmpty(ServerProfileJson))
                {
                    try
                    {
                        _logger.LogDebug("ServerProfileJson raw: {Json}", ServerProfileJson);
                        var options = new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true,
                            ReadCommentHandling = JsonCommentHandling.Skip
                        };

                        var serverProfile = JsonSerializer.Deserialize<SteamCmdProfile>(ServerProfileJson, options);
                        if (serverProfile != null)
                        {
                            _logger.LogInformation("[DEBUG] Profile từ server: Name={0}, AppID={1}, Username={2}, HasPassword={3}",
                                serverProfile.Name, serverProfile.AppID,
                                serverProfile.SteamUsername?.Length > 0 ? "Có" : "Không",
                                serverProfile.SteamPassword?.Length > 0 ? "Có" : "Không");

                            // Kiểm tra xem username và password có null/rỗng không
                            if (string.IsNullOrEmpty(serverProfile.SteamUsername) || string.IsNullOrEmpty(serverProfile.SteamPassword))
                            {
                                _logger.LogWarning("Thông tin tài khoản từ server bị thiếu: Username={HasUsername}, Password={HasPassword}",
                                    !string.IsNullOrEmpty(serverProfile.SteamUsername),
                                    !string.IsNullOrEmpty(serverProfile.SteamPassword));

                                // Nếu thông tin từ server không đầy đủ, sử dụng thông tin nhập trực tiếp nếu có
                                if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
                                {
                                    _logger.LogInformation("Sử dụng thông tin tài khoản từ form thay vì server");
                                    serverProfile.SteamUsername = Username;
                                    serverProfile.SteamPassword = Password;
                                }
                                else
                                {
                                    StatusMessage = "Thông tin tài khoản từ server không đầy đủ. Vui lòng nhập username/password.";
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

                            // Kiểm tra độ dài để xác định dữ liệu có được mã hóa hay không
                            bool usernameEncoded = serverProfile.SteamUsername.Length > 30;
                            bool passwordEncoded = serverProfile.SteamPassword.Length > 30;

                            _logger.LogDebug("Username có vẻ {EncodedStatus}, độ dài {Length}",
                                usernameEncoded ? "đã mã hóa" : "chưa mã hóa",
                                serverProfile.SteamUsername.Length);

                            _logger.LogDebug("Password có vẻ {EncodedStatus}, độ dài {Length}",
                                passwordEncoded ? "đã mã hóa" : "chưa mã hóa",
                                serverProfile.SteamPassword.Length);

                            // Lưu thông tin vào SteamAccounts, mã hóa nếu cần
                            var steamAccount = new SteamAccount
                            {
                                ProfileName = serverProfile.Name,
                                Username = usernameEncoded ? serverProfile.SteamUsername : _encryptionService.Encrypt(serverProfile.SteamUsername),
                                Password = passwordEncoded ? serverProfile.SteamPassword : _encryptionService.Encrypt(serverProfile.SteamPassword),
                                AppIds = serverProfile.AppID,
                                GameNames = serverProfile.Name,
                                CreatedAt = DateTime.Now,
                                UpdatedAt = DateTime.Now
                            };

                            await _steamAccountService.AddAccountAsync(steamAccount);

                            _logger.LogInformation("Đã lưu thông tin tài khoản từ server vào SteamAccounts: Username={0}, AppID={1}",
                                serverProfile.SteamUsername, serverProfile.AppID);

                            // Cập nhật thông tin trong Profile để lưu vào ProfileService
                            Profile.Name = serverProfile.Name;
                            Profile.AppID = serverProfile.AppID;
                            // Không lưu thông tin nhạy cảm vào Profile
                            Profile.SteamUsername = ""; // Để trống vì đã lưu trong SteamAccounts
                            Profile.SteamPassword = ""; // Để trống vì đã lưu trong SteamAccounts

                            StatusMessage = "Đã sử dụng tài khoản và ID game từ server. Thông tin tài khoản đã được lưu an toàn.";
                            IsSuccess = true;
                        }
                        else
                        {
                            _logger.LogWarning("Không thể phân tích ServerProfileJson");
                            StatusMessage = "Không thể đọc thông tin profile từ server. Vui lòng nhập thông tin trực tiếp.";
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
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi deserialize ServerProfileJson hoặc lưu SteamAccount");
                        StatusMessage = "Lỗi khi xử lý thông tin từ server: " + ex.Message;
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
                else if (!string.IsNullOrEmpty(Username) || !string.IsNullOrEmpty(Password))
                {
                    // Mã hóa thông tin đăng nhập Steam nếu có
                    Profile.SteamUsername = Username;
                    Profile.SteamPassword = Password;

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

                // Xử lý thông tin đăng nhập (lưu tài khoản đã mã hóa từ server)
                if (!string.IsNullOrEmpty(serverProfile.SteamUsername) && !string.IsNullOrEmpty(serverProfile.SteamPassword))
                {
                    _logger.LogInformation("Xử lý thông tin đăng nhập từ server cho profile: {0}", serverProfile.Name);
                    
                    try
                    {
                        // Kiểm tra và lưu thông tin tài khoản vào SteamAccount
                        var steamAccount = new SteamAccount
                        {
                            ProfileName = serverProfile.Name,
                            Username = serverProfile.SteamUsername, // Thông tin gốc từ server
                            Password = serverProfile.SteamPassword, // Thông tin gốc từ server
                            AppIds = serverProfile.AppID,
                            GameNames = serverProfile.Name,
                            CreatedAt = DateTime.Now,
                            UpdatedAt = DateTime.Now
                        };
                        
                        // Thêm tài khoản vào SteamAccounts
                        await _steamAccountService.AddAccountAsync(steamAccount);
                        
                        // Kiểm tra xem thông tin đăng nhập có thể giải mã được không
                        // Tạo bản sao để kiểm tra
                        var testAccount = _steamAccountService.DecryptAndReencryptIfNeeded(steamAccount);
                        
                        // Log thông tin kiểm tra
                        if (testAccount != null)
                        {
                            bool usernameDecrypted = testAccount.Username != steamAccount.Username;
                            bool passwordDecrypted = testAccount.Password != steamAccount.Password;
                            
                            _logger.LogInformation("Kết quả kiểm tra giải mã: Username {0}, Password {1}",
                                usernameDecrypted ? "đã giải mã" : "chưa giải mã",
                                passwordDecrypted ? "đã giải mã" : "chưa giải mã");
                        }
                        
                        // Xóa thông tin nhạy cảm khỏi profile chính
                        serverProfile.SteamUsername = string.Empty;
                        serverProfile.SteamPassword = string.Empty;
                        
                        _logger.LogInformation("Đã xử lý và lưu thông tin đăng nhập cho profile {0} vào SteamAccounts", serverProfile.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể xử lý thông tin đăng nhập từ server, sẽ giữ nguyên thông tin gốc");
                    }
                }

                // Thêm profile mới (đã xóa thông tin nhạy cảm)
                await _profileService.AddProfileAsync(serverProfile);

                return new JsonResult(new
                {
                    success = true,
                    message = $"Đã nhập profile '{profileName}' từ server thành công",
                    redirectUrl = "/UpdateManagement"
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

        // Phương thức quét thư mục đệ quy - hỗ trợ symbolic link
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
                    // Kiểm tra xem steamappsDir có phải là symbolic link không
                    var dirInfo = new DirectoryInfo(steamappsDir);
                    bool isSymbolicLink = dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint);

                    if (isSymbolicLink)
                    {
                        _logger.LogInformation("Đã phát hiện symbolic link tại: {Path}", steamappsDir);
                    }

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

                            // Lấy đường dẫn cài đặt (thư mục cha của steamapps)
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

        // Kiểm tra chuỗi có phải là base64 không
        private bool IsLikelyBase64(string input)
        {
            if (string.IsNullOrEmpty(input))
                return false;

            // Chuỗi Base64 phải có độ dài chia hết cho 4
            if (input.Length % 4 != 0)
                return false;

            // Chuỗi Base64 chỉ chứa các ký tự hợp lệ: A-Z, a-z, 0-9, +, /, và = ở cuối
            return System.Text.RegularExpressions.Regex.IsMatch(
                input, @"^[a-zA-Z0-9\+/]*={0,3}$");
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
                var skippedGames = new List<string>();
                var failedGames = new List<string>();

                // Log một số game đầu tiên để debug
                foreach (var game in games.Take(3))
                {
                    _logger.LogInformation("Game nhập vào: Name={Name}, AppId={AppId}, InstallPath={Path}", 
                        game.Name, game.AppId, game.InstallPath);
                }

                // Lấy tất cả tài khoản SteamAccounts để tìm kiếm tài khoản phù hợp
                var allSteamAccounts = await _steamAccountService.GetAllAccountsAsync();

                foreach (var game in games)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(game.AppId) || string.IsNullOrEmpty(game.InstallPath))
                        {
                            _logger.LogWarning("Bỏ qua game thiếu thông tin: AppId={AppId}, InstallPath={InstallPath}", 
                                game.AppId, game.InstallPath);
                            skippedGames.Add(game.Name);
                            continue;
                        }

                        // Kiểm tra xem game đã tồn tại chưa
                        var existingProfiles = await _profileService.GetAllProfiles();
                        bool profileExists = existingProfiles.Any(p => 
                            !string.IsNullOrEmpty(p.AppID) && 
                            p.AppID.Split(',').Select(id => id.Trim()).Contains(game.AppId));
                        
                        if (profileExists)
                        {
                            _logger.LogInformation("Game {Name} (AppId: {AppId}) đã tồn tại, bỏ qua", game.Name, game.AppId);
                            skippedGames.Add(game.Name);
                            continue;
                        }

                        // Tìm tài khoản Steam phù hợp với AppId này
                        SteamAccount matchingAccount = null;
                        foreach (var account in allSteamAccounts)
                        {
                            if (!string.IsNullOrEmpty(account.AppIds))
                            {
                                var appIds = account.AppIds.Split(',').Select(id => id.Trim()).ToList();
                                if (appIds.Contains(game.AppId))
                                {
                                    // Thử giải mã tài khoản nếu cần
                                    matchingAccount = _steamAccountService.DecryptAndReencryptIfNeeded(account);
                                    
                                    // Log kết quả giải mã (che giấu thông tin nhạy cảm)
                                    if (matchingAccount != null)
                                    {
                                        bool usernameDecrypted = matchingAccount.Username != account.Username;
                                        bool passwordDecrypted = matchingAccount.Password != account.Password;
                                        
                                        _logger.LogDebug("Tài khoản: {ProfileName}, Username {UsernameStatus}, Password {PasswordStatus}",
                                            matchingAccount.ProfileName,
                                            usernameDecrypted ? "đã giải mã" : "chưa giải mã",
                                            passwordDecrypted ? "đã giải mã" : "chưa giải mã");
                                    }
                                    
                                    break;
                                }
                            }
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
                            ValidateFiles = true,
                            AutoRun = false,
                            // Không lưu thông tin tài khoản vào profile chính
                            SteamUsername = string.Empty,
                            SteamPassword = string.Empty
                        };

                        // Nếu có tài khoản phù hợp, đã lưu trong SteamAccounts
                        if (matchingAccount != null)
                        {
                            _logger.LogInformation("Tìm thấy tài khoản Steam phù hợp cho AppID {AppId}", game.AppId);
                            // Kiểm tra xem có cả username và password không
                            if (!string.IsNullOrEmpty(matchingAccount.Username) && !string.IsNullOrEmpty(matchingAccount.Password))
                            {
                                _logger.LogDebug("Tài khoản có đầy đủ thông tin đăng nhập: {UsernameHint}***", 
                                    !string.IsNullOrEmpty(matchingAccount.Username) && matchingAccount.Username.Length > 3 ? 
                                    matchingAccount.Username.Substring(0, 3) : "***");
                            }
                            else
                            {
                                _logger.LogWarning("Tài khoản thiếu thông tin đăng nhập: Username={HasUsername}, Password={HasPassword}",
                                    !string.IsNullOrEmpty(matchingAccount.Username),
                                    !string.IsNullOrEmpty(matchingAccount.Password));
                            }
                        }
                        else
                        {
                            _logger.LogInformation("Không tìm thấy tài khoản Steam phù hợp cho AppID {AppId}", game.AppId);
                            
                            // Nếu chưa có tài khoản phù hợp, thêm entry game này vào danh sách game cần tài khoản
                            // Kiểm tra xem có tài khoản "default" hoặc "Auto" chưa để thêm vào
                            var defaultAccount = allSteamAccounts.FirstOrDefault(a =>
                                a.ProfileName.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                                a.ProfileName.Equals("Auto", StringComparison.OrdinalIgnoreCase));

                            if (defaultAccount != null)
                            {
                                // Thêm AppID vào tài khoản default
                                var appIds = defaultAccount.AppIds?.Split(',').ToList() ?? new List<string>();
                                if (!appIds.Contains(game.AppId))
                                {
                                    appIds.Add(game.AppId);
                                    defaultAccount.AppIds = string.Join(",", appIds);

                                    // Thêm tên game vào danh sách
                                    var gameNames = defaultAccount.GameNames?.Split(',').ToList() ?? new List<string>();
                                    if (!gameNames.Contains(game.Name))
                                    {
                                        gameNames.Add(game.Name);
                                        defaultAccount.GameNames = string.Join(",", gameNames);
                                    }

                                    defaultAccount.UpdatedAt = DateTime.Now;
                                    await _steamAccountService.UpdateAccountAsync(defaultAccount);

                                    _logger.LogInformation("Đã thêm AppID {AppId} vào tài khoản Default", game.AppId);
                                }
                            }
                        }

                        // Lưu profile mới
                        await _profileService.AddProfileAsync(profile);
                        importedCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi nhập game {Name} (AppId: {AppId})", game.Name, game.AppId);
                        failedGames.Add(game.Name);
                    }
                }

                string message = $"Đã nhập thành công {importedCount} game";
                if (skippedGames.Count > 0)
                {
                    message += $", bỏ qua {skippedGames.Count} game đã tồn tại";
                }
                if (failedGames.Count > 0)
                {
                    message += $", không thể nhập {failedGames.Count} game do lỗi";
                }

                _logger.LogInformation("Kết quả nhập game: {Message}", message);

                return new JsonResult(new { 
                    success = importedCount > 0, 
                    importedCount, 
                    skippedGames = skippedGames.Count, 
                    failedGames = failedGames.Count,
                    message = message,
                    redirectUrl = "/UpdateManagement"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi nhập tất cả game");
                return new JsonResult(new { success = false, message = $"Lỗi: {ex.Message}" });
            }
        }

        // Handler để kiểm tra xem AppID đã tồn tại trong hệ thống chưa
        public async Task<IActionResult> OnGetCheckAppIdExistsAsync(string appId)
        {
            if (string.IsNullOrEmpty(appId))
            {
                return new JsonResult(new { exists = false });
            }

            try
            {
                var existingProfiles = await _profileService.GetAllProfiles();

                var duplicateProfile = existingProfiles.FirstOrDefault(p =>
                    !string.IsNullOrEmpty(p.AppID) &&
                    p.AppID.Split(',').Select(id => id.Trim()).Contains(appId));

                if (duplicateProfile != null)
                {
                    return new JsonResult(new { exists = true, profileName = duplicateProfile.Name });
                }

                return new JsonResult(new { exists = false });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra AppID {AppId}", appId);
                return new JsonResult(new { exists = false, error = ex.Message });
            }
        }
    }
}