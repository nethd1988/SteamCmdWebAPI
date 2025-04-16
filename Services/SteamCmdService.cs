using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public class SteamCmdService
    {
        private readonly ILogger<SteamCmdService> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly ProfileService _profileService;
        private readonly EncryptionService _encryptionService;
        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _twoFactorTasks = new ConcurrentDictionary<int, TaskCompletionSource<string>>();

        // Regex để trích xuất phần trăm tiến trình
        private static readonly Regex _progressRegex = new Regex(@"(\d+)(?:\.\d+)?%", RegexOptions.Compiled);
        // Regex để lọc các dòng liên quan đến mklink
        private static readonly Regex _mklinkRegex = new Regex(@"symbolic link created|MKLINK", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string ProfileName { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }
        }

        private readonly List<LogEntry> _logs = new List<LogEntry>();
        private readonly object _logsLock = new object();

        public SteamCmdService(
            ILogger<SteamCmdService> logger,
            IHubContext<LogHub> hubContext,
            ProfileService profileService,
            EncryptionService encryptionService)
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _encryptionService = encryptionService;
        }

        public List<LogEntry> GetLogs()
        {
            lock (_logsLock)
            {
                return _logs.ToList();
            }
        }

        public async Task<bool> RunProfileAsync(int profileId)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile với ID {ProfileId}", profileId);
                    return false;
                }

                if (_steamCmdProcesses.TryGetValue(profileId, out var existingProcess) && !existingProcess.HasExited)
                {
                    _logger.LogWarning("Profile {ProfileName} (ID: {ProfileId}) đã đang chạy", profile.Name, profileId);
                    return false;
                }

                await RunSteamCmdAsync(GetSteamCmdPath(), profile, profileId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy profile {ProfileId}", profileId);

                // Thêm vào log
                lock (_logsLock)
                {
                    _logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.Now,
                        ProfileName = (GetProfileNameById(profileId) ?? $"Unknown (ID: {profileId})"),
                        Status = "Error",
                        Message = $"Lỗi: {ex.Message}"
                    });

                    // Giới hạn số lượng log lưu trữ
                    if (_logs.Count > 1000)
                    {
                        _logs.RemoveRange(0, _logs.Count - 1000);
                    }
                }

                return false;
            }
        }

        private string GetProfileNameById(int profileId)
        {
            try
            {
                var profile = _profileService.GetProfileById(profileId).GetAwaiter().GetResult();
                return profile?.Name;
            }
            catch
            {
                return null;
            }
        }

        public async Task RunAllProfilesAsync()
        {
            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.AutoRun))
            {
                try
                {
                    await RunProfileAsync(profile.Id);
                    await Task.Delay(2000); // Delay để tránh quá tải
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi chạy profile {ProfileName} (ID: {ProfileId})", profile.Name, profile.Id);
                }
            }
        }

        public async Task StopProfileAsync(int profileId)
        {
            if (_steamCmdProcesses.TryGetValue(profileId, out var process) && !process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã dừng tiến trình ID {profileId}");

                    // Cập nhật trạng thái profile
                    var profile = await _profileService.GetProfileById(profileId);
                    if (profile != null)
                    {
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi dừng tiến trình profile {ProfileId}", profileId);
                }
            }
        }

        public async Task StopAllProfilesAsync()
        {
            foreach (var kvp in _steamCmdProcesses)
            {
                if (!kvp.Value.HasExited)
                {
                    try
                    {
                        kvp.Value.Kill(entireProcessTree: true);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã dừng tiến trình ID {kvp.Key}");

                        // Cập nhật trạng thái profile
                        var profile = await _profileService.GetProfileById(kvp.Key);
                        if (profile != null)
                        {
                            profile.Status = "Stopped";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi dừng tiến trình {ProcessId}", kvp.Key);
                    }
                }
            }
            _steamCmdProcesses.Clear();
            await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã dừng tất cả tiến trình");
        }

        public async Task SubmitTwoFactorCodeAsync(int profileId, string twoFactorCode)
        {
            try
            {
                _logger.LogInformation("Đang xử lý mã 2FA cho profile ID {ProfileId}", profileId);

                // Gửi mã 2FA đến SteamCMD
                if (_steamCmdProcesses.TryGetValue(profileId, out var process) && !process.HasExited)
                {
                    process.StandardInput.WriteLine(twoFactorCode);
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã gửi mã 2FA đến SteamCMD cho profile ID {profileId}");
                    _logger.LogInformation("Đã gửi mã 2FA thành công cho profile ID {ProfileId}", profileId);
                }
                else
                {
                    _logger.LogWarning("Không tìm thấy tiến trình đang chạy cho profile ID {ProfileId}", profileId);
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Không tìm thấy tiến trình đang chạy cho profile ID {profileId}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý mã 2FA cho profile {ProfileId}", profileId);
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi xử lý mã 2FA: {ex.Message}");
            }
        }

        private async Task RunSteamCmdAsync(string steamCmdPath, SteamCmdProfile profile, int profileId)
        {
            if (!File.Exists(steamCmdPath))
            {
                _logger.LogError("SteamCMD không tồn tại tại đường dẫn: {SteamCmdPath}", steamCmdPath);
                throw new FileNotFoundException("SteamCMD không tồn tại.", steamCmdPath);
            }

            Process steamCmdProcess = null;
            try
            {
                // Log thông tin đăng nhập (không bao gồm mật khẩu thực tế)
                _logger.LogInformation("Profile login info: AnonymousLogin={AnonymousLogin}, HasUsername={HasUsername}, HasPassword={HasPassword}",
                    profile.AnonymousLogin, !string.IsNullOrEmpty(profile.SteamUsername), !string.IsNullOrEmpty(profile.SteamPassword));

                // Tạo symbolic link cho thư mục steamapps nếu cần
                if (!string.IsNullOrEmpty(profile.InstallDirectory))
                {
                    await CreateSteamAppsSymbolicLink(steamCmdPath, profile.InstallDirectory, profileId);
                }

                string arguments = BuildSteamCmdArguments(profile);
                string safeArguments = profile.AnonymousLogin
                    ? arguments
                    : arguments.Replace(_encryptionService.Decrypt(profile.SteamPassword), "********");

                _logger.LogInformation("Chạy SteamCMD với profile {ProfileName} (ID: {ProfileId}), Arguments: {Arguments}",
                    profile.Name, profileId, safeArguments);

                steamCmdProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = steamCmdPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };

                // Cập nhật trạng thái profile
                profile.Status = "Running";
                profile.StartTime = DateTime.Now;
                profile.Pid = 0; // Sẽ cập nhật sau khi process thực sự chạy
                await _profileService.UpdateProfile(profile);

                var tcs = new TaskCompletionSource<bool>();
                string lastProgressLog = string.Empty;

                steamCmdProcess.Exited += async (sender, args) =>
                {
                    try
                    {
                        var updatedProfile = await _profileService.GetProfileById(profileId);
                        if (updatedProfile != null)
                        {
                            updatedProfile.Status = "Stopped";
                            updatedProfile.StopTime = DateTime.Now;
                            updatedProfile.LastRun = DateTime.Now;
                            updatedProfile.Pid = 0;
                            await _profileService.UpdateProfile(updatedProfile);
                        }

                        _steamCmdProcesses.TryRemove(profileId, out _);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Tiến trình profile {profile.Name} đã kết thúc");

                        // Thêm vào log
                        lock (_logsLock)
                        {
                            _logs.Add(new LogEntry
                            {
                                Timestamp = DateTime.Now,
                                ProfileName = profile.Name,
                                Status = "Success",
                                Message = $"Hoàn thành chạy profile"
                            });

                            // Giới hạn số lượng log lưu trữ
                            if (_logs.Count > 1000)
                            {
                                _logs.RemoveRange(0, _logs.Count - 1000);
                            }
                        }

                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý sự kiện Exited cho profile {ProfileName} (ID: {ProfileId})", profile.Name, profileId);
                        tcs.TrySetException(ex);
                    }
                };

                _steamCmdProcesses.TryAdd(profileId, steamCmdProcess);

                steamCmdProcess.OutputDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // Lọc bỏ log liên quan đến mklink
                        if (_mklinkRegex.IsMatch(e.Data))
                        {
                            _logger.LogDebug("Bỏ qua log mklink: {Output}", e.Data);
                            return;
                        }

                        // Xử lý thông tin tiến trình
                        var progressMatch = _progressRegex.Match(e.Data);
                        if (progressMatch.Success)
                        {
                            string progressLog = $"Tiến trình: {progressMatch.Groups[1].Value}%";

                            // Chỉ hiển thị log khi phần trăm thay đổi để tránh spam
                            if (progressLog != lastProgressLog)
                            {
                                await _hubContext.Clients.All.SendAsync("ReceiveLog", progressLog);
                                lastProgressLog = progressLog;
                            }
                        }
                        else
                        {
                            _logger.LogDebug("SteamCMD output: {Output}", e.Data);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", e.Data);
                        }

                        if (e.Data.Contains("Steam Guard code") || e.Data.Contains("Two-factor code"))
                        {
                            _logger.LogInformation("Phát hiện yêu cầu mã Steam Guard cho profile {ProfileName} (ID: {ProfileId})", profile.Name, profileId);

                            // Thông báo cho client yêu cầu nhập mã 2FA
                            await _hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);

                            // Tạo task để đợi mã 2FA
                            var twoFactorTcs = new TaskCompletionSource<string>();
                            _twoFactorTasks.TryAdd(profileId, twoFactorTcs);

                            // Đợi mã 2FA trong tối đa 2 phút
                            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
                            var completedTask = await Task.WhenAny(twoFactorTcs.Task, timeoutTask);

                            if (completedTask == twoFactorTcs.Task)
                            {
                                string twoFactorCode = await twoFactorTcs.Task;
                                _logger.LogInformation("Đã nhận mã 2FA cho profile {ProfileName} (ID: {ProfileId})", profile.Name, profileId);

                                if (!string.IsNullOrEmpty(twoFactorCode))
                                {
                                    // Gửi mã 2FA đến SteamCMD
                                    steamCmdProcess.StandardInput.WriteLine(twoFactorCode);
                                    await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã gửi mã 2FA đến SteamCMD");
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Hết thời gian chờ mã 2FA cho profile {ProfileName} (ID: {ProfileId})", profile.Name, profileId);
                                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Hết thời gian chờ mã 2FA");
                            }

                            // Dọn dẹp
                            _twoFactorTasks.TryRemove(profileId, out _);
                        }
                    }
                };

                steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        // Lọc bỏ log liên quan đến mklink
                        if (_mklinkRegex.IsMatch(e.Data))
                        {
                            _logger.LogDebug("Bỏ qua log mklink error: {Output}", e.Data);
                            return;
                        }

                        _logger.LogError("SteamCMD error: {Error}", e.Data);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"LỖI: {e.Data}");
                    }
                };

                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Bắt đầu chạy profile {profile.Name}");

                bool started = steamCmdProcess.Start();
                if (!started)
                {
                    throw new InvalidOperationException($"Không thể khởi động SteamCMD cho profile {profile.Name}");
                }

                // Cập nhật PID sau khi process đã chạy
                profile.Pid = steamCmdProcess.Id;
                await _profileService.UpdateProfile(profile);

                steamCmdProcess.BeginOutputReadLine();
                steamCmdProcess.BeginErrorReadLine();

                // Đợi tiến trình kết thúc hoặc timeout
                var processCompletionTask = tcs.Task;
                var processTimeoutTask = Task.Delay(TimeSpan.FromHours(2)); // Timeout sau 2 giờ

                var completedTask = await Task.WhenAny(processCompletionTask, processTimeoutTask);
                if (completedTask == processTimeoutTask)
                {
                    _logger.LogWarning("Tiến trình SteamCMD cho profile {ProfileName} (ID: {ProfileId}) đã chạy quá lâu", profile.Name, profileId);

                    if (!steamCmdProcess.HasExited)
                    {
                        steamCmdProcess.Kill(entireProcessTree: true);
                        _logger.LogInformation("Đã dừng tiến trình SteamCMD cho profile {ProfileName} (ID: {ProfileId}) do timeout", profile.Name, profileId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD cho profile {ProfileName} (ID: {ProfileId})", profile.Name, profileId);

                // Cập nhật trạng thái profile
                profile.Status = "Error";
                profile.StopTime = DateTime.Now;
                await _profileService.UpdateProfile(profile);

                // Thêm vào log
                lock (_logsLock)
                {
                    _logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.Now,
                        ProfileName = profile.Name,
                        Status = "Error",
                        Message = $"Lỗi: {ex.Message}"
                    });

                    // Giới hạn số lượng log lưu trữ
                    if (_logs.Count > 1000)
                    {
                        _logs.RemoveRange(0, _logs.Count - 1000);
                    }
                }

                throw;
            }
        }

        private async Task CreateSteamAppsSymbolicLink(string steamCmdPath, string installDirectory, int profileId)
        {
            try
            {
                // Lấy thư mục chứa steamcmd.exe
                string steamCmdDir = Path.GetDirectoryName(steamCmdPath);

                // Tạo đường dẫn đến thư mục steamapps trong thư mục cài đặt game
                string targetSteamAppsPath = Path.Combine(installDirectory, "steamapps");

                // Tạo đường dẫn đến thư mục steamapps trong thư mục steamcmd
                string linkSteamAppsPath = Path.Combine(steamCmdDir, "steamapps");

                // Đảm bảo thư mục cài đặt game tồn tại
                if (!Directory.Exists(installDirectory))
                {
                    _logger.LogInformation("Tạo thư mục cài đặt game: {InstallDirectory}", installDirectory);
                    Directory.CreateDirectory(installDirectory);
                }

                // Đảm bảo thư mục steamapps trong thư mục cài đặt game tồn tại
                if (!Directory.Exists(targetSteamAppsPath))
                {
                    _logger.LogInformation("Tạo thư mục steamapps trong thư mục cài đặt game: {SteamAppsPath}", targetSteamAppsPath);
                    Directory.CreateDirectory(targetSteamAppsPath);
                }

                // Kiểm tra và xử lý symbolic link cũ
                if (Directory.Exists(linkSteamAppsPath))
                {
                    try
                    {
                        // Nếu là symbolic link, xóa liên kết
                        if (IsSymbolicLink(linkSteamAppsPath))
                        {
                            _logger.LogInformation("Xóa symbolic link cũ: {LinkPath}", linkSteamAppsPath);
                            RemoveSymbolicLink(linkSteamAppsPath);
                        }
                        else
                        {
                            // Nếu là thư mục thật, đổi tên nó
                            string backupDir = Path.Combine(steamCmdDir, "steamapps_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                            _logger.LogInformation("Di chuyển thư mục steamapps cũ sang {BackupDir}", backupDir);
                            Directory.Move(linkSteamAppsPath, backupDir);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý thư mục steamapps hiện tại: {Error}", ex.Message);
                        // Tiếp tục thực hiện với phương pháp khác
                        try
                        {
                            if (Directory.Exists(linkSteamAppsPath))
                                Directory.Delete(linkSteamAppsPath, true);
                        }
                        catch
                        {
                            // Bỏ qua nếu không thể xóa
                        }
                    }
                }

                try
                {
                    // Tạo symbolic link mới
                    _logger.LogInformation("Tạo symbolic link từ {LinkPath} đến {TargetPath}", linkSteamAppsPath, targetSteamAppsPath);
                    CreateSymbolicLink(linkSteamAppsPath, targetSteamAppsPath);

                    _logger.LogInformation("Đã tạo symbolic link thành công cho thư mục steamapps");
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã tạo symbolic link cho thư mục steamapps đến {installDirectory}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tạo symbolic link: {Error}", ex.Message);

                    // Thử phương pháp thay thế: Tạo shortcut hoặc copy thư mục
                    _logger.LogInformation("Thử phương pháp thay thế: copy thư mục");

                    // Tạo thư mục steamapps nếu chưa tồn tại
                    if (!Directory.Exists(linkSteamAppsPath))
                        Directory.CreateDirectory(linkSteamAppsPath);

                    await _hubContext.Clients.All.SendAsync("ReceiveLog", "Không thể tạo symbolic link, đang sử dụng phương pháp thay thế...");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý thư mục steamapps: {Message}", ex.Message);
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi xử lý thư mục steamapps: {ex.Message}");
            }
        }

        private bool IsSymbolicLink(string path)
        {
            try
            {
                FileAttributes attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        private void RemoveSymbolicLink(string linkPath)
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }
        }

        private void CreateSymbolicLink(string linkPath, string targetPath)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // Phương pháp 1: Sử dụng mklink thông qua cmd
                    Process process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c mklink /D \"{linkPath}\" \"{targetPath}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            Verb = "runas" // Yêu cầu quyền admin
                        }
                    };

                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("Lỗi khi tạo symbolic link (mklink): {Error}", error);
                        throw new Exception($"Lỗi tạo symbolic link (mklink): {error}");
                    }

                    _logger.LogInformation("Tạo symbolic link thành công: {Output}", output);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tạo symbolic link qua cmd");

                    try
                    {
                        // Phương pháp 2: Sử dụng API của Windows
                        string command = $"New-Item -ItemType SymbolicLink -Path \"{linkPath}\" -Target \"{targetPath}\" -Force";
                        ProcessStartInfo startInfo = new ProcessStartInfo
                        {
                            FileName = "powershell",
                            Arguments = $"-Command \"{command}\"",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            Verb = "runas" // Yêu cầu quyền admin
                        };

                        using var process = Process.Start(startInfo);
                        process.WaitForExit();

                        if (process.ExitCode != 0)
                        {
                            string error = process.StandardError.ReadToEnd();
                            _logger.LogError("Lỗi khi tạo symbolic link qua PowerShell: {Error}", error);
                            throw new Exception($"Lỗi tạo symbolic link (PowerShell): {error}");
                        }

                        _logger.LogInformation("Tạo symbolic link thành công qua PowerShell");
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogError(ex2, "Lỗi khi tạo symbolic link qua PowerShell");

                        // Phương pháp 3: Tạo junction (thay thế cho symbolic link)
                        _logger.LogInformation("Thử tạo junction thay cho symbolic link");
                        try
                        {
                            if (!Directory.Exists(targetPath))
                                Directory.CreateDirectory(targetPath);

                            // Chỉ tạo junction nếu thư mục đích đã tồn tại
                            if (Directory.Exists(linkPath))
                                Directory.Delete(linkPath);

                            Process junctionProcess = new Process
                            {
                                StartInfo = new ProcessStartInfo
                                {
                                    FileName = "cmd.exe",
                                    Arguments = $"/c mklink /J \"{linkPath}\" \"{targetPath}\"",
                                    UseShellExecute = false,
                                    CreateNoWindow = true,
                                    RedirectStandardOutput = true,
                                    RedirectStandardError = true
                                }
                            };

                            junctionProcess.Start();
                            junctionProcess.WaitForExit();

                            if (junctionProcess.ExitCode != 0)
                            {
                                string error = junctionProcess.StandardError.ReadToEnd();
                                _logger.LogError("Lỗi khi tạo junction: {Error}", error);
                                throw new Exception($"Không thể tạo junction: {error}");
                            }

                            _logger.LogInformation("Đã tạo junction thành công");
                        }
                        catch (Exception ex3)
                        {
                            _logger.LogError(ex3, "Không thể tạo junction");
                            throw new Exception("Không thể tạo symbolic link hoặc junction", ex3);
                        }
                    }
                }
            }
            else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                Process process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ln",
                        Arguments = $"-s \"{targetPath}\" \"{linkPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    _logger.LogError("Lỗi khi tạo symbolic link trên Linux: {Error}", error);
                    throw new Exception($"Không thể tạo symbolic link: {error}");
                }
            }
            else
            {
                throw new PlatformNotSupportedException("Hệ điều hành không được hỗ trợ");
            }
        }

        private void MoveDirectoryContents(string sourceDir, string targetDir)
        {
            // Đảm bảo thư mục đích tồn tại
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Di chuyển tất cả các file
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string fileName = Path.GetFileName(file);
                string destFile = Path.Combine(targetDir, fileName);

                // Xóa file đích nếu đã tồn tại
                if (File.Exists(destFile))
                {
                    File.Delete(destFile);
                }

                File.Move(file, destFile);
            }

            // Di chuyển tất cả các thư mục con
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                string dirName = Path.GetFileName(dir);
                string destDir = Path.Combine(targetDir, dirName);

                // Đệ quy di chuyển nội dung thư mục con
                MoveDirectoryContents(dir, destDir);
            }
        }

        private string BuildSteamCmdArguments(SteamCmdProfile profile)
        {
            StringBuilder argumentBuilder = new StringBuilder();

            // Thêm thông tin đăng nhập
            if (profile.AnonymousLogin)
            {
                _logger.LogInformation("Sử dụng đăng nhập ẩn danh cho profile: {ProfileName}", profile.Name);
                argumentBuilder.Append("+login anonymous ");
            }
            else
            {
                try
                {
                    string username = !string.IsNullOrEmpty(profile.SteamUsername) ?
                        _encryptionService.Decrypt(profile.SteamUsername) : "";
                    string password = !string.IsNullOrEmpty(profile.SteamPassword) ?
                        _encryptionService.Decrypt(profile.SteamPassword) : "";

                    if (string.IsNullOrEmpty(username))
                    {
                        _logger.LogWarning("Tên đăng nhập rỗng, chuyển sang đăng nhập ẩn danh cho profile {ProfileName}", profile.Name);
                        argumentBuilder.Append("+login anonymous ");
                    }
                    else
                    {
                        argumentBuilder.Append($"+login {username} {password} ");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi giải mã thông tin đăng nhập: {Message}", ex.Message);
                    _logger.LogInformation("Chuyển sang đăng nhập ẩn danh do lỗi giải mã");
                    argumentBuilder.Append("+login anonymous ");
                }
            }

            // Thêm lệnh cập nhật ứng dụng
            argumentBuilder.Append($"+app_update {profile.AppID} ");

            // Thêm xác thực file nếu được yêu cầu
            if (profile.ValidateFiles)
            {
                argumentBuilder.Append("validate ");
            }

            // Thêm tham số bổ sung nếu có
            if (!string.IsNullOrEmpty(profile.Arguments))
            {
                argumentBuilder.Append($"{profile.Arguments} ");
            }

            // Thêm lệnh quit để SteamCMD tự động thoát khi hoàn thành
            argumentBuilder.Append("+quit");

            return argumentBuilder.ToString();
        }

        public async Task ShutdownAsync()
        {
            try
            {
                await StopAllProfilesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả tiến trình trong quá trình shutdown");
            }
        }

        // Thêm phương thức này vào lớp SteamCmdService
        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(AppContext.BaseDirectory, "steamcmd");
            string steamCmdPath = Path.Combine(steamCmdDir, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");

            // Kiểm tra xem steamcmd đã tồn tại chưa
            if (!File.Exists(steamCmdPath))
            {
                // Tự động tải và cài đặt SteamCMD
                if (OperatingSystem.IsWindows())
                {
                    DownloadAndInstallSteamCmdWindows(steamCmdDir).Wait();
                }
                else if (OperatingSystem.IsLinux())
                {
                    DownloadAndInstallSteamCmdLinux(steamCmdDir).Wait();
                }
                else
                {
                    throw new PlatformNotSupportedException("Chỉ hỗ trợ Windows và Linux");
                }
            }

            return steamCmdPath;
        }

        private async Task DownloadAndInstallSteamCmdWindows(string steamCmdDir)
        {
            _logger.LogInformation("Tự động tải và cài đặt SteamCMD cho Windows...");

            // URL tải SteamCMD
            string steamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";
            string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");

            try
            {
                // Đảm bảo thư mục tồn tại
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd tại {Directory}", steamCmdDir);
                }

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(10); // Tăng timeout để xử lý tập tin lớn

                    // Tải file zip
                    _logger.LogInformation("Đang tải SteamCMD từ {0}...", steamCmdUrl);

                    // Hiển thị tiến trình tải
                    var progress = new Progress<float>(percent =>
                    {
                        _hubContext.Clients.All.SendAsync("ReceiveLog", $"Tải SteamCMD: {percent:F2}%").Wait();
                    });

                    // Tạo tác vụ tải file
                    var downloadTask = DownloadFileWithProgressAsync(httpClient, steamCmdUrl, zipPath, progress);
                    await downloadTask;

                    // Giải nén file
                    _logger.LogInformation("Đang giải nén SteamCMD...");
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đang giải nén SteamCMD...");
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, steamCmdDir, true);

                    // Xóa file zip
                    File.Delete(zipPath);

                    _logger.LogInformation("Đã cài đặt SteamCMD thành công");
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã cài đặt SteamCMD thành công");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải và cài đặt SteamCMD: {Message}", ex.Message);
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi tải SteamCMD: {ex.Message}");
                throw;
            }
        }

        private async Task DownloadFileWithProgressAsync(System.Net.Http.HttpClient client, string url, string destinationPath, IProgress<float> progress)
        {
            // Tạo các thư mục cần thiết
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));

            // Tải xuống tệp tin
            using (var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var totalReadBytes = 0L;
                var buffer = new byte[81920]; // 80 KB buffer
                var isMoreToRead = true;

                using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true))
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                {
                    do
                    {
                        var readBytes = await contentStream.ReadAsync(buffer);
                        if (readBytes == 0)
                        {
                            isMoreToRead = false;
                        }
                        else
                        {
                            await fileStream.WriteAsync(buffer, 0, readBytes);

                            totalReadBytes += readBytes;
                            if (totalBytes > 0 && progress != null)
                            {
                                var progressPercent = (float)totalReadBytes / totalBytes * 100;
                                progress.Report(progressPercent);
                            }
                        }
                    }
                    while (isMoreToRead);
                }
            }
        }

        private async Task DownloadAndInstallSteamCmdLinux(string steamCmdDir)
        {
            _logger.LogInformation("Tự động tải và cài đặt SteamCMD cho Linux...");

            // URL tải SteamCMD
            string steamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
            string tarPath = Path.Combine(steamCmdDir, "steamcmd_linux.tar.gz");

            try
            {
                // Đảm bảo thư mục tồn tại
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd tại {Directory}", steamCmdDir);
                }

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(10); // Tăng timeout để xử lý tập tin lớn

                    // Tải file tar.gz
                    _logger.LogInformation("Đang tải SteamCMD từ {0}...", steamCmdUrl);

                    // Hiển thị tiến trình tải
                    var progress = new Progress<float>(percent =>
                    {
                        _hubContext.Clients.All.SendAsync("ReceiveLog", $"Tải SteamCMD: {percent:F2}%").Wait();
                    });

                    // Tạo tác vụ tải file
                    var downloadTask = DownloadFileWithProgressAsync(httpClient, steamCmdUrl, tarPath, progress);
                    await downloadTask;

                    // Giải nén file bằng lệnh hệ thống
                    _logger.LogInformation("Đang giải nén SteamCMD...");
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đang giải nén SteamCMD...");

                    Process process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "tar",
                            Arguments = $"-xzf {tarPath} -C {steamCmdDir}",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    await process.WaitForExitAsync();

                    // Cấp quyền thực thi
                    process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = $"+x {Path.Combine(steamCmdDir, "steamcmd.sh")}",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();
                    await process.WaitForExitAsync();

                    // Xóa file tar.gz
                    File.Delete(tarPath);

                    _logger.LogInformation("Đã cài đặt SteamCMD thành công");
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã cài đặt SteamCMD thành công");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải và cài đặt SteamCMD: {Message}", ex.Message);
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi khi tải SteamCMD: {ex.Message}");
                throw;
            }
        }
    }
}