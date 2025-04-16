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

        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string ProfileName { get; set; }
            public string Status { get; set; }
            public string Message { get; set; }
        }

        private readonly List<LogEntry> _logs = new List<LogEntry>();

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
            return _logs;
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
                _logs.Add(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    ProfileName = (await _profileService.GetProfileById(profileId))?.Name ?? $"Unknown (ID: {profileId})",
                    Status = "Error",
                    Message = $"Lỗi: {ex.Message}"
                });

                return false;
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
                process.Kill();
                await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã dừng tiến trình ID {profileId}");
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
                        kvp.Value.Kill();
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Đã dừng tiến trình ID {kvp.Key}");
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
                string arguments = BuildSteamCmdArguments(profile);
                _logger.LogInformation("Chạy SteamCMD với profile {ProfileName} (ID: {ProfileId})", profile.Name, profileId);

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
                        _logs.Add(new LogEntry
                        {
                            Timestamp = DateTime.Now,
                            ProfileName = profile.Name,
                            Status = "Success",
                            Message = $"Hoàn thành chạy profile"
                        });

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
                        _logger.LogDebug("SteamCMD output: {Output}", e.Data);
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", e.Data);

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
                        steamCmdProcess.Kill();
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
                _logs.Add(new LogEntry
                {
                    Timestamp = DateTime.Now,
                    ProfileName = profile.Name,
                    Status = "Error",
                    Message = $"Lỗi: {ex.Message}"
                });

                throw;
            }
        }

        public async Task SubmitTwoFactorCodeAsync(int profileId, string twoFactorCode)
        {
            if (_twoFactorTasks.TryRemove(profileId, out var tcs))
            {
                tcs.SetResult(twoFactorCode);
            }
        }

        private string BuildSteamCmdArguments(SteamCmdProfile profile)
        {
            StringBuilder argumentBuilder = new StringBuilder();

            // Thêm thông tin đăng nhập
            if (profile.AnonymousLogin)
            {
                argumentBuilder.Append("+login anonymous ");
            }
            else
            {
                string username = _encryptionService.Decrypt(profile.SteamUsername);
                string password = _encryptionService.Decrypt(profile.SteamPassword);

                if (string.IsNullOrEmpty(username))
                {
                    throw new InvalidOperationException("Tên đăng nhập không được cung cấp và không sử dụng đăng nhập ẩn danh");
                }

                argumentBuilder.Append($"+login {username} {password} ");
            }

            // Thêm lệnh cập nhật ứng dụng
            argumentBuilder.Append($"+app_update {profile.AppID} ");

            // Thêm xác thực file nếu được yêu cầu
            if (profile.ValidateFiles)
            {
                argumentBuilder.Append("validate ");
            }

            // Thêm thư mục cài đặt nếu được cung cấp
            if (!string.IsNullOrEmpty(profile.InstallDirectory))
            {
                argumentBuilder.Append($"+force_install_dir \"{profile.InstallDirectory}\" ");
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

            using (var httpClient = new HttpClient())
            {
                // Tải file zip
                _logger.LogInformation("Đang tải SteamCMD từ {0}...", steamCmdUrl);
                byte[] zipData = await httpClient.GetByteArrayAsync(steamCmdUrl);
                await File.WriteAllBytesAsync(zipPath, zipData);

                // Giải nén file
                _logger.LogInformation("Đang giải nén SteamCMD...");
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, steamCmdDir, true);

                // Xóa file zip
                File.Delete(zipPath);

                _logger.LogInformation("Đã cài đặt SteamCMD thành công");
            }
        }

        private async Task DownloadAndInstallSteamCmdLinux(string steamCmdDir)
        {
            _logger.LogInformation("Tự động tải và cài đặt SteamCMD cho Linux...");

            // URL tải SteamCMD
            string steamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
            string tarPath = Path.Combine(steamCmdDir, "steamcmd_linux.tar.gz");

            using (var httpClient = new HttpClient())
            {
                // Tải file tar.gz
                _logger.LogInformation("Đang tải SteamCMD từ {0}...", steamCmdUrl);
                byte[] tarData = await httpClient.GetByteArrayAsync(steamCmdUrl);
                await File.WriteAllBytesAsync(tarPath, tarData);

                // Giải nén file bằng lệnh hệ thống
                _logger.LogInformation("Đang giải nén SteamCMD...");
                Process process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "tar",
                        Arguments = $"-xzf {tarPath} -C {steamCmdDir}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
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
                        UseShellExecute = false
                    }
                };

                process.Start();
                await process.WaitForExitAsync();

                // Xóa file tar.gz
                File.Delete(tarPath);

                _logger.LogInformation("Đã cài đặt SteamCMD thành công");
            }
        }
    }
}