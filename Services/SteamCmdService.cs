using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Timers;
using System.Text;

namespace SteamCmdWebAPI.Services
{
    public class SteamCmdService
    {
        private readonly ILogger<SteamCmdService> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly ProfileService _profileService;
        private readonly SettingsService _settingsService;
        private readonly EncryptionService _encryptionService;
        private readonly LogFileReader _logFileReader;

        // Giới hạn số lượng log để tránh tràn bộ nhớ
        private const int MaxLogEntries = 5000;

        // Danh sách các tiến trình SteamCMD đang chạy, theo dõi theo profile ID
        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private SemaphoreSlim _profileSemaphore = new SemaphoreSlim(1, 1);
        private readonly object _processLock = new object();

        private readonly System.Timers.Timer _scheduleTimer;
        private volatile bool _isRunningAllProfiles = false;
        private int _currentProfileIndex = 0;
        private volatile bool _cancelAutoRun = false;

        // Thêm biến class member
        private DateTime _lastAutoRunTime = DateTime.MinValue;

        // Danh sách để lưu trữ log chi tiết
        private readonly List<LogEntry> _logs = new List<LogEntry>(MaxLogEntries);

        // Class để lưu trữ log chi tiết
        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string ProfileName { get; set; }
            public string Status { get; set; } // "Success", "Error", hoặc "Info"
            public string Message { get; set; }

            public LogEntry(DateTime timestamp, string profileName, string status, string message)
            {
                Timestamp = timestamp;
                ProfileName = profileName;
                Status = status;
                Message = message;
            }
        }

        public SteamCmdService(
            ILogger<SteamCmdService> logger,
            IHubContext<LogHub> hubContext,
            ProfileService profileService,
            SettingsService settingsService,
            EncryptionService encryptionService,
            LogFileReader logFileReader)
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _settingsService = settingsService;
            _encryptionService = encryptionService;
            _logFileReader = logFileReader;

            // Khởi tạo timer để kiểm tra lịch hẹn
            _scheduleTimer = new System.Timers.Timer(60000); // Kiểm tra mỗi phút
            _scheduleTimer.Elapsed += async (s, e) => await CheckScheduleAsync();
            _scheduleTimer.AutoReset = true;
            _scheduleTimer.Start();
            _logger.LogInformation("Schedule timer started.");
        }

        // Phương thức thêm log với giới hạn kích thước
        private void AddLog(LogEntry entry)
        {
            lock (_logs)
            {
                _logs.Add(entry);
                // Xóa log cũ nếu vượt quá giới hạn
                if (_logs.Count > MaxLogEntries)
                {
                    _logs.RemoveRange(0, _logs.Count - MaxLogEntries);
                }
            }
        }

        // Cập nhật lại phương thức CheckScheduleAsync
        private async Task CheckScheduleAsync()
        {
            try
            {
                var settings = await _settingsService.LoadSettingsAsync();
                if (!settings.AutoRunEnabled || _isRunningAllProfiles)
                {
                    return;
                }

                var now = DateTime.Now;
                // Kiểm tra dựa theo khoảng thời gian
                TimeSpan timeSinceLastRun = now - _lastAutoRunTime;
                int intervalHours = settings.AutoRunIntervalHours;

                if (_lastAutoRunTime == DateTime.MinValue || timeSinceLastRun.TotalHours >= intervalHours)
                {
                    _logger.LogInformation("Đang chạy tất cả profile theo khoảng thời gian {0} giờ", intervalHours);
                    await RunAllProfilesAsync();
                    _lastAutoRunTime = now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kiểm tra lịch hẹn");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi kiểm tra lịch hẹn: {ex.Message}");
            }
        }

        // Hàm hỗ trợ gửi log an toàn
        private async Task SafeSendLogAsync(string profileName, string status, string message)
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveLog", message);
                AddLog(new LogEntry(DateTime.Now, profileName, status, message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi log: {Message}", ex.Message);
            }
        }

        #region Process Management

        // Sửa lại phương thức StartProfileAsync
        public async Task<bool> StartProfileAsync(int profileId)
        {
            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogError("Không tìm thấy profile ID {ProfileId}", profileId);
                    await SafeSendLogAsync($"Profile {profileId}", "Error", $"Không tìm thấy profile với ID {profileId}");
                    return false;
                }

                await SafeSendLogAsync(profile.Name, "Info", $"Đang chuẩn bị chạy {profile.Name}...");

                // Đảm bảo không có tiến trình cũ đang chạy
                KillAllSteamCmdProcessesImmediate();

                // Xóa symlink cũ nếu có
                await RemoveSteamAppsSymLink();

                // Chờ lấy semaphore để tránh chạy đồng thời
                if (!await _profileSemaphore.WaitAsync(TimeSpan.FromSeconds(60)))
                {
                    _logger.LogError("Không thể lấy semaphore sau 60 giây, hủy thao tác");
                    await SafeSendLogAsync(profile.Name, "Error", "Không thể khởi động profile (timeout)");
                    return false;
                }

                try
                {
                    // Cập nhật trạng thái profile
                    profile.Status = "Running";
                    profile.StartTime = DateTime.Now;
                    profile.Pid = 0;
                    await _profileService.UpdateProfile(profile);

                    await SafeSendLogAsync(profile.Name, "Info", $"Đang khởi động {profile.Name}...");

                    // Kiểm tra và cài đặt SteamCMD nếu cần
                    bool steamCmdInstalled = await IsSteamCmdInstalled();
                    if (!steamCmdInstalled)
                    {
                        await SafeSendLogAsync(profile.Name, "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                        await InstallSteamCmd();
                    }

                    if (!await IsSteamCmdInstalled())
                    {
                        string errorMsg = "Không thể cài đặt hoặc tìm thấy SteamCMD";
                        _logger.LogError(errorMsg);
                        await SafeSendLogAsync(profile.Name, "Error", errorMsg);

                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        return false;
                    }

                    // Tạo thư mục cài đặt nếu cần
                    if (!string.IsNullOrEmpty(profile.InstallDirectory) && !Directory.Exists(profile.InstallDirectory))
                    {
                        try
                        {
                            Directory.CreateDirectory(profile.InstallDirectory);
                            _logger.LogInformation("Đã tạo thư mục cài đặt: {Directory}", profile.InstallDirectory);
                            await SafeSendLogAsync(profile.Name, "Info", $"Đã tạo thư mục cài đặt: {profile.InstallDirectory}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Không thể tạo thư mục cài đặt: {Directory}", profile.InstallDirectory);
                            throw new Exception($"Không thể tạo thư mục cài đặt {profile.InstallDirectory}: {ex.Message}");
                        }
                    }

                    // Chạy SteamCMD
                    await RunSteamCmdAsync(GetSteamCmdPath(), profile, profileId);

                    // Cập nhật trạng thái khi hoàn thành
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0;
                    profile.LastRun = DateTime.UtcNow;
                    await _profileService.UpdateProfile(profile);

                    await SafeSendLogAsync(profile.Name, "Success", $"Đã chạy {profile.Name} thành công");
                    return true;
                }
                finally
                {
                    _profileSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy profile {ProfileId}: {Message}", profileId, ex.Message);

                try
                {
                    var profile = await _profileService.GetProfileById(profileId);
                    if (profile != null)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy {profile.Name}: {ex.Message}");
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        profile.Pid = 0;
                        await _profileService.UpdateProfile(profile);
                    }
                }
                catch { }

                // Dọn dẹp tài nguyên
                await RemoveSteamAppsSymLink();
                KillAllSteamCmdProcessesImmediate();
                _steamCmdProcesses.TryRemove(profileId, out _);

                return false;
            }
        }

        private void KillAllSteamCmdProcessesImmediate()
        {
            try
            {
                // Đảm bảo clear dictionary _steamCmdProcesses trước
                var currentProcesses = _steamCmdProcesses.ToArray();
                _steamCmdProcesses.Clear();

                foreach (var kvp in currentProcesses)
                {
                    var profileId = kvp.Key;
                    var process = kvp.Value;
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            _logger.LogInformation("Đang kill ngay lập tức tiến trình SteamCMD với PID {Pid} cho profile {ProfileId}",
                                process.Id, profileId);

                            process.Kill(true);
                            process.WaitForExit(2000);

                            if (!process.HasExited)
                            {
                                _logger.LogWarning("Tiến trình không kết thúc sau 2s, tiếp tục chờ tiến trình thoát");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi khi kill tiến trình SteamCMD cho profile {ProfileId}", profileId);
                    }
                    finally
                    {
                        process?.Dispose();
                    }
                }

                try
                {
                    foreach (var process in Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")))
                    {
                        try
                        {
                            _logger.LogWarning("Phát hiện tiến trình SteamCMD thất lạc với PID {Pid}, đang kill", process.Id);
                            process.Kill(true);
                            process.WaitForExit(2000);
                            process.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi kill tiến trình SteamCMD thất lạc với PID {Pid}", process.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tìm và kill các tiến trình SteamCMD thất lạc");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kill tất cả tiến trình SteamCMD ngay lập tức");
            }
        }

        public async Task StopAllProfilesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các profiles...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profiles...");

            _cancelAutoRun = true;

            var profileIds = _steamCmdProcesses.Keys.ToList();

            foreach (var profileId in profileIds)
            {
                await StopAndCleanupProfileAsync(profileId);
            }

            await KillOrphanedSteamCmdProcesses();

            // Đảm bảo cập nhật tất cả trạng thái trong database
            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.Status == "Running"))
            {
                _logger.LogInformation("Cập nhật trạng thái profile {ProfileName} (ID: {ProfileId}) từ Running thành Stopped",
                    profile.Name, profile.Id);

                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);
            }

            await RemoveSteamAppsSymLink();

            // Xóa mọi quy chiếu tới processes trong dictionary
            _steamCmdProcesses.Clear();

            await SafeSendLogAsync("System", "Success", "Đã dừng tất cả các profiles");
        }

        private void KillAllSteamCmdProcesses()
        {
            try
            {
                foreach (var kvp in _steamCmdProcesses)
                {
                    var process = kvp.Value;
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            process.Kill();
                            if (!process.WaitForExit(10000))
                            {
                                _logger.LogWarning("Tiến trình không kết thúc sau 10s cho profile {0}", kvp.Key);
                            }
                            _logger.LogInformation("Đã kill tiến trình steamcmd với PID {0} cho profile {1}", process.Id, kvp.Key);
                            _ = SafeSendLogAsync("System", "Info", $"Đã kill tiến trình steamcmd với PID {process.Id} cho profile {kvp.Key}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi khi kill tiến trình steamcmd với PID {0} cho profile {1}",
                            process?.Id ?? 0, kvp.Key);
                    }
                    finally
                    {
                        process?.Dispose();
                    }
                }

                _steamCmdProcesses.Clear();

                KillOrphanedSteamCmdProcesses().Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kill tất cả tiến trình steamcmd");
                throw new Exception($"Lỗi khi kill tất cả tiến trình steamcmd: {ex.Message}");
            }
        }

        private async Task StopAndCleanupProfileAsync(int profileId)
        {
            if (_steamCmdProcesses.TryRemove(profileId, out var existingProcess))
            {
                try
                {
                    if (existingProcess != null && !existingProcess.HasExited)
                    {
                        _logger.LogInformation("Đang dừng tiến trình đang chạy cho profile {ProfileId}, PID: {Pid}",
                            profileId, existingProcess.Id);
                        await SafeSendLogAsync($"Profile {profileId}", "Info", $"Đang dừng tiến trình đang chạy (PID: {existingProcess.Id})");

                        existingProcess.Kill(true);
                        existingProcess.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileId}", profileId);
                    // Không gửi thông báo lỗi đến client
                }
                finally
                {
                    existingProcess?.Dispose();
                }
            }

            await RemoveSteamAppsSymLink();
            await KillOrphanedSteamCmdProcesses();
        }

        private async Task KillOrphanedSteamCmdProcesses()
        {
            try
            {
                var steamCmdProcesses = Process.GetProcessesByName("steamcmd");
                if (steamCmdProcesses.Length > 0)
                {
                    _logger.LogWarning("Phát hiện {Count} tiến trình SteamCMD bị thất lạc, đang dọn dẹp", steamCmdProcesses.Length);
                    await SafeSendLogAsync("System", "Warning", $"Phát hiện {steamCmdProcesses.Length} tiến trình SteamCMD bị thất lạc, đang dọn dẹp");

                    foreach (var process in steamCmdProcesses)
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                _logger.LogInformation("Đang kill tiến trình SteamCMD thất lạc, PID: {Pid}", process.Id);
                                process.Kill(true);
                                process.WaitForExit(2000);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi kill tiến trình SteamCMD thất lạc, PID: {Pid}", process.Id);
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tìm và kill các tiến trình SteamCMD thất lạc");
            }
        }

        #endregion

        #region SteamCMD Installation and Management

        public async Task<bool> RunProfileAsync(int id)
        {
            try
            {
                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    await SafeSendLogAsync($"Profile {id}", "Error", $"Không tìm thấy cấu hình với ID {id}");
                    _logger.LogWarning("Không tìm thấy cấu hình với ID {Id}", id);
                    return false;
                }

                return await StartProfileAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD cho profile ID {Id}", id);
                await SafeSendLogAsync($"Profile {id}", "Error", $"Lỗi: {ex.Message}");

                await StopAndCleanupProfileAsync(id);
                throw;
            }
        }

        // Sửa phương thức RunAllProfilesAsync
        // Sửa phương thức RunAllProfilesAsync
        public async Task RunAllProfilesAsync()
        {
            if (!await _profileSemaphore.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                await SafeSendLogAsync("System", "Error", "Không thể khởi động tất cả profile (hệ thống đang bận)");
                return;
            }

            try
            {
                var profiles = await _profileService.GetAllProfiles();
                if (!profiles.Any())
                {
                    _logger.LogWarning("Không có cấu hình nào để chạy");
                    await SafeSendLogAsync("System", "Error", "Không có cấu hình nào để chạy");
                    return;
                }

                _isRunningAllProfiles = true;
                _cancelAutoRun = false;
                _currentProfileIndex = 0;

                await SafeSendLogAsync("System", "Info", "Bắt đầu chạy tất cả các profile...");

                // Đảm bảo dừng tất cả tiến trình và cập nhật trạng thái trước
                await StopAllProfilesAsync();

                // Đảm bảo trạng thái cập nhật trong database
                foreach (var profile in profiles)
                {
                    if (profile.Status == "Running")
                    {
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        profile.Pid = 0;
                        await _profileService.UpdateProfile(profile);
                    }
                }

                await Task.Delay(3000); // Chờ đợi đủ lâu để đảm bảo tất cả đã dừng

                // Khởi động lại SteamCMD nếu cần
                KillAllSteamCmdProcessesImmediate();
                await RemoveSteamAppsSymLink();

                // Lấy lại danh sách cấu hình sau khi dừng tất cả
                profiles = await _profileService.GetAllProfiles();

                // Chạy từng profile một
                for (int i = 0; i < profiles.Count && !_cancelAutoRun; i++)
                {
                    _currentProfileIndex = i;
                    var profile = profiles[i];

                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy profile ({i + 1}/{profiles.Count}): {profile.Name}");

                    try
                    {
                        await StartProfileAsync(profile.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi chạy profile {ProfileName} (ID: {ProfileId})",
                            profile.Name, profile.Id);
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy profile: {ex.Message}");

                        // Đảm bảo cập nhật trạng thái khi có lỗi
                        try
                        {
                            var failedProfile = await _profileService.GetProfileById(profile.Id);
                            if (failedProfile != null && failedProfile.Status == "Running")
                            {
                                failedProfile.Status = "Stopped";
                                failedProfile.StopTime = DateTime.Now;
                                failedProfile.Pid = 0;
                                await _profileService.UpdateProfile(failedProfile);
                            }
                        }
                        catch { }
                    }

                    // Đợi một chút trước khi chạy profile tiếp theo
                    if (i < profiles.Count - 1)
                    {
                        await Task.Delay(5000);
                    }
                }

                _isRunningAllProfiles = false;
                await SafeSendLogAsync("System", "Success", "Đã hoàn thành chạy tất cả các profile");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tất cả các profile");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chạy tất cả các profile: {ex.Message}");

                // Đảm bảo cập nhật trạng thái tất cả profile
                try
                {
                    var profiles = await _profileService.GetAllProfiles();
                    foreach (var profile in profiles)
                    {
                        if (profile.Status == "Running")
                        {
                            profile.Status = "Stopped";
                            profile.StopTime = DateTime.Now;
                            profile.Pid = 0;
                            await _profileService.UpdateProfile(profile);
                        }
                    }
                }
                catch { }
            }
            finally
            {
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
                _profileSemaphore.Release();
            }
        }
        private async Task RunNextProfileAsync()
        {
            if (!_isRunningAllProfiles || _cancelAutoRun)
            {
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
                return;
            }

            var profiles = await _profileService.GetAllProfiles();
            if (_currentProfileIndex >= profiles.Count)
            {
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
                await SafeSendLogAsync("System", "Success", "Đã hoàn thành chạy tất cả các profile");
                return;
            }

            var profile = profiles[_currentProfileIndex];
            await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy profile ({_currentProfileIndex + 1}/{profiles.Count}): {profile.Name}");
            await StartProfileAsync(profile.Id);
        }

        public Task<bool> IsSteamCmdInstalled()
        {
            string steamCmdPath = GetSteamCmdPath();
            bool exists = File.Exists(steamCmdPath);
            _logger.LogInformation("Kiểm tra SteamCMD tại {Path}: {Exists}", steamCmdPath, exists);

            if (exists)
            {
                _logger.LogInformation("SteamCMD đã được cài đặt tại: {Path}", steamCmdPath);
            }
            else
            {
                _logger.LogWarning("Không tìm thấy SteamCMD tại: {Path}", steamCmdPath);
            }

            return Task.FromResult(exists);
        }

        public async Task InstallSteamCmd()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            try
            {
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd: {Directory}", steamCmdDir);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamcmd: {steamCmdDir}");
                }

                string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");
                string downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

                if (!OperatingSystem.IsWindows())
                {
                    downloadUrl = OperatingSystem.IsLinux()
                        ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz"
                        : OperatingSystem.IsMacOS()
                            ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_osx.tar.gz"
                            : throw new PlatformNotSupportedException("Hệ điều hành không được hỗ trợ");
                }

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);

                    await SafeSendLogAsync("System", "Info", $"Đang tải SteamCMD từ {downloadUrl}...");
                    _logger.LogInformation("Bắt đầu tải SteamCMD từ {Url}", downloadUrl);

                    var response = await httpClient.GetAsync(downloadUrl);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception($"Không thể tải SteamCMD. Mã trạng thái: {response.StatusCode}");
                    }

                    using (var fs = new FileStream(zipPath, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }

                    await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                    _logger.LogInformation("Đã tải xong SteamCMD vào {Path}", zipPath);
                }

                bool extractSuccess = await ExtractSteamCmdAsync(zipPath, steamCmdDir);
                if (!extractSuccess)
                {
                    throw new Exception("Không thể giải nén SteamCMD");
                }

                await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công");
                _logger.LogInformation("Đã giải nén SteamCMD vào {Directory}", steamCmdDir);

                try
                {
                    File.Delete(zipPath);
                    _logger.LogInformation("Đã xóa file zip: {Path}", zipPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể xóa file zip: {Path}", zipPath);
                }

                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    await SetExecutablePermission(GetSteamCmdPath());
                }

                string steamCmdExe = GetSteamCmdPath();
                if (!File.Exists(steamCmdExe))
                {
                    throw new Exception($"Cài đặt thất bại. Không tìm thấy {steamCmdExe} sau khi cài đặt.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình cài đặt SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi cài đặt SteamCMD: {ex.Message}");
                throw;
            }
        }

        private async Task<bool> ExtractSteamCmdAsync(string zipPath, string destinationFolder)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, destinationFolder, true);
                    return true;
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    using (var process = new Process())
                    {
                        process.StartInfo = new ProcessStartInfo
                        {
                            FileName = "tar",
                            Arguments = $"-xzf \"{zipPath}\" -C \"{destinationFolder}\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };

                        process.Start();

                        var outputTask = process.StandardOutput.ReadToEndAsync();
                        var errorTask = process.StandardError.ReadToEndAsync();

                        if (!process.WaitForExit(60000))
                        {
                            process.Kill();
                            throw new TimeoutException("Quá thời gian khi giải nén SteamCMD.");
                        }

                        string error = await errorTask;
                        if (process.ExitCode != 0)
                        {
                            throw new Exception($"Lỗi khi giải nén SteamCMD: {error}");
                        }

                        return true;
                    }
                }
                throw new PlatformNotSupportedException("Hệ điều hành không được hỗ trợ");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi giải nén SteamCMD");
                throw;
            }
        }

        private async Task SetExecutablePermission(string filePath)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    process.Start();

                    if (!process.WaitForExit(10000))
                    {
                        process.Kill();
                        _logger.LogWarning("Quá thời gian khi thiết lập quyền thực thi cho SteamCMD");
                        return;
                    }

                    if (process.ExitCode != 0)
                    {
                        string error = await process.StandardError.ReadToEndAsync();
                        _logger.LogWarning("Không thể cấp quyền thực thi cho SteamCMD: {Error}", error);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Không thể cấp quyền thực thi cho SteamCMD");
            }
        }

        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string executable = OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh";
            string path = Path.Combine(steamCmdDir, executable);

            _logger.LogInformation("SteamCMD path: {Path}", path);

            return path;
        }

        private async Task CreateSteamAppsSymLink(string gameInstallDir)
        {
            string gameSteamAppsPath = Path.Combine(gameInstallDir, "steamapps");
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            try
            {
                await RemoveSteamAppsSymLink();

                if (!Directory.Exists(gameSteamAppsPath))
                {
                    Directory.CreateDirectory(gameSteamAppsPath);
                    _logger.LogInformation("Đã tạo thư mục steamapps tại {Path}", gameSteamAppsPath);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamapps tại {gameSteamAppsPath}");
                }

                _logger.LogInformation("Tạo symbolic link từ {Source} đến {Target}", gameSteamAppsPath, localSteamAppsPath);

                bool success = false;

                if (OperatingSystem.IsWindows())
                {
                    try
                    {
                        if (Directory.CreateSymbolicLink(localSteamAppsPath, gameSteamAppsPath) != null)
                        {
                            success = true;
                            _logger.LogInformation("Đã tạo symbolic link từ {Source} đến {Target} bằng API .NET", gameSteamAppsPath, localSteamAppsPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể sử dụng Directory.CreateSymbolicLink, sẽ thử dùng process");
                    }
                }

                if (!success)
                {
                    success = await CreateSymbolicLinkWithProcess(gameSteamAppsPath, localSteamAppsPath);

                    if (success)
                    {
                        _logger.LogInformation("Đã tạo symbolic link từ {Source} đến {Target} bằng process", gameSteamAppsPath, localSteamAppsPath);
                    }
                    else
                    {
                        _logger.LogError("Không thể tạo symbolic link sau nhiều lần thử");
                        throw new Exception("Không thể tạo symbolic link sau nhiều lần thử");
                    }
                }

                await SafeSendLogAsync("System", "Info", $"Đã tạo symbolic link từ {gameSteamAppsPath} đến {localSteamAppsPath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo symbolic link: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi tạo symbolic link: {ex.Message}");
                throw;
            }
        }

        private bool IsSymbolicLink(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                return (attr & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch
            {
                return false;
            }
        }

        private void DeleteSymbolicLink(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.Delete(path);
            }
            else
            {
                RunProcessWithTimeout("rm", $"-f \"{path}\"", 10000);
            }
        }

        private async Task<bool> CreateSymbolicLinkWithProcess(string targetPath, string linkPath)
        {
            try
            {
                ProcessStartInfo linkInfo;
                if (OperatingSystem.IsWindows())
                {
                    linkInfo = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/C mklink /D \"{linkPath}\" \"{targetPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }
                else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    linkInfo = new ProcessStartInfo
                    {
                        FileName = "ln",
                        Arguments = $"-s \"{targetPath}\" \"{linkPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                }
                else
                {
                    throw new PlatformNotSupportedException("Hệ điều hành không được hỗ trợ để tạo symbolic link");
                }

                using (var process = Process.Start(linkInfo))
                {
                    if (process == null)
                    {
                        throw new Exception("Không thể khởi động tiến trình để tạo symbolic link.");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(30000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("Quá thời gian khi tạo symbolic link.");
                    }

                    string output = await outputTask;
                    string error = await errorTask;

                    if (process.ExitCode != 0)
                    {
                        _logger.LogError("Lỗi khi tạo symbolic link: {Error}", error);
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tạo symbolic link với process");
                return false;
            }
        }

        private void RunProcessWithTimeout(string fileName, string arguments, int timeoutMs)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    process.Start();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        _logger.LogWarning("Quá thời gian chạy process {FileName} {Arguments}", fileName, arguments);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy process {FileName} {Arguments}", fileName, arguments);
            }
        }

        private async Task RemoveSteamAppsSymLink()
        {
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            try
            {
                if (Directory.Exists(localSteamAppsPath))
                {
                    try
                    {
                        if (IsSymbolicLink(localSteamAppsPath))
                        {
                            _logger.LogInformation("Đang xóa symbolic link tại {Path}", localSteamAppsPath);
                            DeleteSymbolicLink(localSteamAppsPath);
                            _logger.LogInformation("Đã xóa symbolic link tại {Path}", localSteamAppsPath);
                        }
                        else
                        {
                            _logger.LogInformation("Đường dẫn {Path} không phải là symbolic link, bỏ qua", localSteamAppsPath);
                        }
                        await SafeSendLogAsync("System", "Info", $"Đã xóa symbolic link tại {localSteamAppsPath}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Không thể xóa symbolic link theo cách thông thường, thử phương pháp thay thế");
                        try
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                RunProcessWithTimeout("cmd.exe", $"/C rmdir \"{localSteamAppsPath}\"", 10000);
                            }
                            else
                            {
                                RunProcessWithTimeout("rm", $"-f \"{localSteamAppsPath}\"", 10000);
                            }
                            await SafeSendLogAsync("System", "Info", $"Đã xóa symbolic link tại {localSteamAppsPath}");
                        }
                        catch (Exception innerEx)
                        {
                            _logger.LogError(innerEx, "Không thể xóa symbolic link bằng phương pháp thay thế");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xóa symbolic link tại {Path}: {Message}", localSteamAppsPath, ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi xóa symbolic link tại {localSteamAppsPath}: {ex.Message}");
            }
        }

        private string GetSteamCmdLogPath(int profileId)
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string logsDir = Path.Combine(steamCmdDir, "logs");

            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            return Path.Combine(logsDir, "console_log");
        }

        private async Task RunSteamCmdAsync(string steamCmdPath, SteamCmdProfile profile, int profileId)
        {
            Process steamCmdProcess = null;

            if (string.IsNullOrEmpty(steamCmdPath))
                throw new ArgumentNullException(nameof(steamCmdPath));
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrEmpty(profile.InstallDirectory))
                throw new ArgumentNullException(nameof(profile.InstallDirectory));
            if (string.IsNullOrEmpty(profile.Name))
                throw new ArgumentNullException(nameof(profile.Name));

            if (!File.Exists(steamCmdPath))
            {
                _logger.LogError("File SteamCMD không tồn tại tại {Path}", steamCmdPath);
                throw new FileNotFoundException($"File SteamCMD không tồn tại tại {steamCmdPath}");
            }

            await CreateSteamAppsSymLink(profile.InstallDirectory);

            string logFilePath = GetSteamCmdLogPath(profileId);

            // Đảm bảo thư mục logs tồn tại
            string logsDir = Path.GetDirectoryName(logFilePath);
            if (!Directory.Exists(logsDir))
            {
                Directory.CreateDirectory(logsDir);
            }

            _logFileReader.StartMonitoring(logFilePath, async (content) => {
                if (!string.IsNullOrEmpty(content))
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", content);
                }
            });

            // Khai báo biến steamCmdProcesses ở đầu phương thức
            ConcurrentDictionary<int, Process> steamCmdProcesses = _steamCmdProcesses;


            try
            {
                // Kill các tiến trình SteamCMD thất lạc
                try
                {
                    foreach (var process in Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")))
                    {
                        try
                        {
                            _logger.LogWarning("Phát hiện tiến trình SteamCMD thất lạc với PID {Pid}, đang kill", process.Id);
                            process.Kill(true);
                            process.WaitForExit(2000);
                            process.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi kill tiến trình SteamCMD thất lạc với PID {Pid}", process.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tìm và kill các tiến trình SteamCMD thất lạc");
                }

                string loginCommand;
                string usernameToUse = "anonymous";
                string passwordToUse = "";

                if (profile.AnonymousLogin)
                {
                    loginCommand = "+login anonymous";
                }
                else
                {
                    if (!string.IsNullOrEmpty(profile.SteamUsername))
                    {
                        try
                        {
                            usernameToUse = _encryptionService.Decrypt(profile.SteamUsername);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã SteamUsername cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã tên đăng nhập: {ex.Message}");
                            usernameToUse = "anonymous";
                        }
                    }

                    if (!string.IsNullOrEmpty(profile.SteamPassword))
                    {
                        try
                        {
                            passwordToUse = _encryptionService.Decrypt(profile.SteamPassword);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã SteamPassword cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã mật khẩu: {ex.Message}");
                            passwordToUse = "";
                        }
                    }

                    loginCommand = string.IsNullOrEmpty(passwordToUse)
                        ? $"+login {usernameToUse}"
                        : $"+login {usernameToUse} {passwordToUse}";
                }

                string validateArg = profile.ValidateFiles ? "validate" : "";
                string argumentsArg = string.IsNullOrEmpty(profile.Arguments) ? "" : profile.Arguments.Trim();
                string arguments = $"{loginCommand} +app_update {profile.AppID} {validateArg} {argumentsArg} +quit".Trim();

                string safeArguments = !string.IsNullOrEmpty(passwordToUse) && !profile.AnonymousLogin
                    ? arguments.Replace(passwordToUse, "********")
                    : arguments;

                _logger.LogInformation("Chạy SteamCMD với tham số: {SafeArguments}", safeArguments);
                await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy SteamCMD cho {profile.Name}...");
                await SafeSendLogAsync(profile.Name, "Info", $"Lệnh: {safeArguments}");

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
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    },
                    EnableRaisingEvents = true
                };

                var outputBuffer = new StringBuilder();
                var outputTimer = new System.Timers.Timer(100);
                outputTimer.Elapsed += async (sender, e) =>
                {
                    string output;
                    lock (outputBuffer)
                    {
                        if (outputBuffer.Length == 0) return;
                        output = outputBuffer.ToString();
                        outputBuffer.Clear();
                    }
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", output);
                };
                outputTimer.Start();

                steamCmdProcess.OutputDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogDebug("SteamCMD Output: {Data}", e.Data);

                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(e.Data);
                        }
                    }
                };

                steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogError("SteamCMD Error: {Data}", e.Data);

                        await _hubContext.Clients.All.SendAsync("ReceiveLog", e.Data);
                    }
                };

                steamCmdProcesses[profileId] = steamCmdProcess;

                steamCmdProcess.Start();

                profile.Pid = steamCmdProcess.Id;
                await _profileService.UpdateProfile(profile);

                steamCmdProcess.BeginOutputReadLine();
                steamCmdProcess.BeginErrorReadLine();

                await steamCmdProcess.WaitForExitAsync();

                outputTimer.Stop();
                string remainingOutput;
                lock (outputBuffer)
                {
                    remainingOutput = outputBuffer.ToString();
                    outputBuffer.Clear();
                }
                if (!string.IsNullOrEmpty(remainingOutput))
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", remainingOutput);
                }

                _logger.LogInformation("SteamCMD process đã kết thúc với exit code: {ExitCode}", steamCmdProcess.ExitCode);

                string exitMessage = $"SteamCMD đã kết thúc với mã: {steamCmdProcess.ExitCode} ({(steamCmdProcess.ExitCode == 0 ? "Thành công" : "Lỗi")})";
                await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);

                AddLog(new LogEntry(DateTime.Now, profile.Name,
                    steamCmdProcess.ExitCode == 0 ? "Success" : "Error",
                    exitMessage));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD: {ex.Message}");
                throw;
            }
            finally
            {
                _logFileReader.StopMonitoring();

                steamCmdProcesses.TryRemove(profileId, out _);

                await RemoveSteamAppsSymLink();

                if (steamCmdProcess != null)
                {
                    try
                    {
                        if (!steamCmdProcess.HasExited)
                        {
                            steamCmdProcess.Kill();
                            steamCmdProcess.WaitForExit(10000);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileId}", profileId);
                    }
                    finally
                    {
                        steamCmdProcess.Dispose();
                    }
                }
            }
        }

        #endregion

        #region Helper Classes and Methods

        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các process trước khi tắt ứng dụng...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các process trước khi tắt ứng dụng...");

            await StopAllProfilesAsync();

            _scheduleTimer.Stop();
            _scheduleTimer.Dispose();
        }

        public async Task<bool> RestartProfileAsync(int profileId)
        {
            await StopAndCleanupProfileAsync(profileId);

            await Task.Delay(2000);

            return await StartProfileAsync(profileId);
        }

        public async Task StartAllAutoRunProfilesAsync()
        {
            var profiles = await _profileService.GetAllProfiles();
            var autoRunProfiles = profiles.Where(p => p.AutoRun).ToList();

            if (!autoRunProfiles.Any())
            {
                _logger.LogInformation("Không có profile nào được đánh dấu Auto Run");
                return;
            }

            _logger.LogInformation("Đang khởi động {Count} profile Auto Run", autoRunProfiles.Count);
            await SafeSendLogAsync("System", "Info", $"Đang khởi động {autoRunProfiles.Count} profile Auto Run");

            foreach (var profile in autoRunProfiles)
            {
                try
                {
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang tự động khởi động {profile.Name}...");

                    await StopAndCleanupProfileAsync(profile.Id);

                    await StartProfileAsync(profile.Id);

                    await Task.Delay(3000);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi tự động khởi động profile {ProfileName}: {Message}",
                        profile.Name, ex.Message);
                    await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi tự động khởi động: {ex.Message}");
                }
            }

            await SafeSendLogAsync("System", "Success", "Hoàn tất khởi động các profile Auto Run");
        }

        public List<LogEntry> GetLogs()
        {
            lock (_logs)
            {
                return new List<LogEntry>(_logs);
            }
        }

        public void ClearOldLogs(int retainCount = 1000)
        {
            lock (_logs)
            {
                if (_logs.Count > retainCount)
                {
                    _logs.RemoveRange(0, _logs.Count - retainCount);
                }
            }
        }

        #endregion
    }
}