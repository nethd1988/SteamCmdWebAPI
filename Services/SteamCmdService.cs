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

namespace SteamCmdWebAPI.Services
{
    public class SteamCmdService
    {
        private readonly ILogger<SteamCmdService> _logger;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly ProfileService _profileService;
        private readonly SettingsService _settingsService;
        private readonly EncryptionService _encryptionService;

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
            EncryptionService encryptionService)
        {
            _logger = logger;
            _hubContext = hubContext;
            _profileService = profileService;
            _settingsService = settingsService;
            _encryptionService = encryptionService;

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

        // Sửa đổi trong phương thức StartProfileAsync trong file Services/SteamCmdService.cs
        public async Task<bool> StartProfileAsync(int profileId)
        {
            try
            {
                // Lấy profile để hiển thị thông báo trước khi đợi semaphore
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogError("Không tìm thấy profile ID {ProfileId}", profileId);
                    await SafeSendLogAsync($"Profile {profileId}", "Error", $"Không tìm thấy profile với ID {profileId}");
                    return false;
                }

                // Thông báo rằng đang chuẩn bị chạy profile mới
                await SafeSendLogAsync(profile.Name, "Info", $"Đang chuẩn bị chạy {profile.Name}...");

                // Kill tất cả các tiến trình SteamCMD ngay lập tức TRƯỚC khi đợi semaphore
                // Điều này ngăn chặn tình trạng chờ đợi quá lâu khi có một tiến trình đang chạy
                KillAllSteamCmdProcessesImmediate();

                // Xóa symbolic link trước khi đợi semaphore
                await RemoveSteamAppsSymLink();

                // Khóa để đảm bảo thao tác process là atomic - với timeout để tránh deadlock
                if (!await _profileSemaphore.WaitAsync(TimeSpan.FromSeconds(30)))
                {
                    _logger.LogError("Không thể lấy semaphore sau 30 giây, thử khởi tạo lại semaphore");
                    // Reset semaphore nếu không thể đợi
                    _profileSemaphore.Dispose();
                    _profileSemaphore = new SemaphoreSlim(1, 1);
                    await _profileSemaphore.WaitAsync(TimeSpan.FromSeconds(5));
                }

                try
                {
                    // Dừng tất cả các tiến trình một lần nữa để đảm bảo không có tiến trình nào còn chạy
                    KillAllSteamCmdProcessesImmediate();

                    // Đợi một khoảng thời gian ngắn để đảm bảo hệ thống đã dọn dẹp
                    await Task.Delay(500);

                    // Xóa symbolic link một lần nữa
                    await RemoveSteamAppsSymLink();

                    // Cập nhật trạng thái profile thành Running
                    profile.Status = "Running";
                    profile.StartTime = DateTime.Now;
                    profile.Pid = 0; // Sẽ cập nhật PID sau khi chạy
                    await _profileService.UpdateProfile(profile);

                    await SafeSendLogAsync(profile.Name, "Info", $"Đang khởi động {profile.Name}...");

                    // Kiểm tra và cài đặt SteamCMD
                    bool steamCmdInstalled = await IsSteamCmdInstalled();
                    if (!steamCmdInstalled)
                    {
                        await SafeSendLogAsync(profile.Name, "Info", "SteamCMD chưa được cài đặt. Đang tải về...");
                        await InstallSteamCmd();
                    }

                    // Kiểm tra lại sau khi cài đặt
                    if (!await IsSteamCmdInstalled())
                    {
                        string errorMsg = "Không thể cài đặt hoặc tìm thấy SteamCMD";
                        _logger.LogError(errorMsg);
                        await SafeSendLogAsync(profile.Name, "Error", errorMsg);

                        // Cập nhật trạng thái profile về Stopped
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        return false;
                    }

                    // Tạo thư mục cài đặt nếu chưa tồn tại
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

                    // Chạy SteamCMD với tên profile
                    await RunSteamCmdAsync(GetSteamCmdPath(), profile, profileId);

                    // Cập nhật trạng thái profile sau khi chạy
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

                // Đảm bảo cập nhật trạng thái profile về Stopped nếu có lỗi
                var profile = await _profileService.GetProfileById(profileId);
                if (profile != null)
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy {profile.Name}: {ex.Message}");
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0;
                    await _profileService.UpdateProfile(profile);
                }

                // Đảm bảo xóa symbolic link nếu có lỗi
                await RemoveSteamAppsSymLink();

                // Kill tất cả các tiến trình SteamCMD
                KillAllSteamCmdProcessesImmediate();

                // Xóa tiến trình khỏi danh sách nếu có lỗi
                _steamCmdProcesses.TryRemove(profileId, out _);
                return false;
            }
        }

        // Thêm phương thức mới để kill tất cả các tiến trình SteamCMD ngay lập tức
        private void KillAllSteamCmdProcessesImmediate()
        {
            try
            {
                // Dừng tất cả các tiến trình trong danh sách ngay lập tức
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    var profileId = kvp.Key;
                    var process = kvp.Value;
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            _logger.LogInformation("Đang kill ngay lập tức tiến trình SteamCMD với PID {Pid} cho profile {ProfileId}",
                                process.Id, profileId);

                            process.Kill(true); // Dùng Kill(true) để kill cả các process con
                            process.WaitForExit(2000); // Chỉ đợi 2 giây

                            if (!process.HasExited)
                            {
                                _logger.LogWarning("Tiến trình không kết thúc sau 2s, tiếp tục chờ tiến trình thoát");
                                // Không chờ tiếp nữa, tiếp tục tiến trình
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
                        _steamCmdProcesses.TryRemove(profileId, out _);
                    }
                }

                // Tìm và kill các tiến trình SteamCMD khác không thuộc danh sách
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

            _cancelAutoRun = true; // Dừng chạy tự động nếu đang chạy

            // Lấy danh sách profile ID
            var profileIds = _steamCmdProcesses.Keys.ToList();

            // Dừng từng profile
            foreach (var profileId in profileIds)
            {
                await StopAndCleanupProfileAsync(profileId);
            }

            // Đảm bảo tất cả các tiến trình thất lạc cũng bị kill
            await KillOrphanedSteamCmdProcesses();

            // Cập nhật trạng thái tất cả profile thành Stopped
            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.Status == "Running"))
            {
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);
            }

            // Xóa symbolic link
            await RemoveSteamAppsSymLink();

            await SafeSendLogAsync("System", "Success", "Đã dừng tất cả các profiles");
        }

        private void KillAllSteamCmdProcesses()
        {
            try
            {
                // Dừng tất cả các tiến trình trong danh sách
                foreach (var kvp in _steamCmdProcesses)
                {
                    var process = kvp.Value;
                    try
                    {
                        if (process != null && !process.HasExited)
                        {
                            process.Kill();
                            if (!process.WaitForExit(10000)) // 10 giây timeout
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

                // Xóa tất cả các tiến trình khỏi danh sách
                _steamCmdProcesses.Clear();

                // Kill các tiến trình thất lạc
                KillOrphanedSteamCmdProcesses().Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kill tất cả tiến trình steamcmd");
                throw new Exception($"Lỗi khi kill tất cả tiến trình steamcmd: {ex.Message}");
            }
        }
        // Phương thức để dừng tiến trình và dọn dẹp
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

                        // Kill process
                        existingProcess.Kill(true); // Kill cả process con
                        if (!existingProcess.WaitForExit(5000)) // 5 giây timeout
                        {
                            _logger.LogWarning("Tiến trình không tự kết thúc, buộc phải kill (Profile: {ProfileId}, PID: {Pid})",
                                profileId, existingProcess.Id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileId}", profileId);
                }
                finally
                {
                    existingProcess?.Dispose();
                }
            }

            // Xóa symbolic link
            await RemoveSteamAppsSymLink();

            // Tìm và kill thêm các tiến trình SteamCMD bị thất lạc
            await KillOrphanedSteamCmdProcesses();
        }

        // Tìm và kill các tiến trình SteamCMD bị thất lạc
        private async Task KillOrphanedSteamCmdProcesses()
        {
            try
            {
                // Lấy danh sách tất cả các tiến trình SteamCMD đang chạy
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
                                process.Kill(true); // Kill cả process con
                                process.WaitForExit(2000); // 2 giây timeout
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

                // Sử dụng method StartProfileAsync để chạy profile
                return await StartProfileAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD cho profile ID {Id}", id);
                await SafeSendLogAsync($"Profile {id}", "Error", $"Lỗi: {ex.Message}");

                // Đảm bảo dọn dẹp khi có lỗi
                await StopAndCleanupProfileAsync(id);
                throw;
            }
        }

        public async Task RunAllProfilesAsync()
        {
            // Sử dụng SemaphoreSlim để đảm bảo chỉ một quá trình chạy tất cả profiles tại một thời điểm
            await _profileSemaphore.WaitAsync();
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

                // Dừng tất cả các tiến trình trước khi bắt đầu
                await StopAllProfilesAsync();

                while (_currentProfileIndex < profiles.Count && !_cancelAutoRun)
                {
                    var profile = profiles[_currentProfileIndex];
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy profile ({_currentProfileIndex + 1}/{profiles.Count}): {profile.Name}");
                    await StartProfileAsync(profile.Id);
                    _currentProfileIndex++;
                    await Task.Delay(2000); // Đợi 2 giây trước khi chạy profile tiếp theo
                }

                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
                await SafeSendLogAsync("System", "Success", "Đã hoàn thành chạy tất cả các profile");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tất cả các profile");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chạy tất cả các profile: {ex.Message}");
                _isRunningAllProfiles = false;
                _cancelAutoRun = false;
            }
            finally
            {
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
                    httpClient.Timeout = TimeSpan.FromMinutes(5); // Đặt timeout 5 phút

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

                // Kiểm tra lại sau khi cài đặt
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

                        if (!process.WaitForExit(60000)) // 60 giây timeout
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

                    if (!process.WaitForExit(10000)) // 10 giây timeout
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

            // Ghi log đường dẫn
            _logger.LogInformation("SteamCMD path: {Path}", path);

            return path;
        }

        private async Task CreateSteamAppsSymLink(string gameInstallDir)
        {
            string gameSteamAppsPath = Path.Combine(gameInstallDir, "steamapps");
            string localSteamAppsPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            try
            {
                // Xóa symlink cũ (nếu có) trước khi tạo mới
                await RemoveSteamAppsSymLink();

                // Tạo thư mục steamapps trong thư mục cài đặt game nếu chưa tồn tại
                if (!Directory.Exists(gameSteamAppsPath))
                {
                    Directory.CreateDirectory(gameSteamAppsPath);
                    _logger.LogInformation("Đã tạo thư mục steamapps tại {Path}", gameSteamAppsPath);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamapps tại {gameSteamAppsPath}");
                }

                _logger.LogInformation("Tạo symbolic link từ {Source} đến {Target}", gameSteamAppsPath, localSteamAppsPath);

                bool success = false;

                // Thử sử dụng API .NET
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

                // Nếu API .NET không thành công, thử dùng process
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
                    // Sử dụng mklink trên Windows
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
                    // Sử dụng ln -s trên Linux/macOS
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

                    if (!process.WaitForExit(30000)) // 30 giây timeout
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
                        // Xử lý riêng cho symbolic link
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

        private async Task RunSteamCmdAsync(string steamCmdPath, SteamCmdProfile profile, int profileId)
        {
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

            // Tạo symbolic link trước khi chạy SteamCMD
            await CreateSteamAppsSymLink(profile.InstallDirectory);

            Process steamCmdProcess = null;
            try
            {
                // Tạo lệnh SteamCMD
                string loginCommand;
                string usernameToUse = "anonymous";
                string passwordToUse = "";

                if (profile.AnonymousLogin)
                {
                    loginCommand = "+login anonymous";
                }
                else
                {
                    // Giải mã username nếu có
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
                            usernameToUse = "anonymous"; // Sử dụng anonymous nếu giải mã thất bại
                        }
                    }

                    // Giải mã password nếu có
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

                // Ẩn mật khẩu trong log
                string safeArguments = !string.IsNullOrEmpty(passwordToUse) && !profile.AnonymousLogin
                    ? arguments.Replace(passwordToUse, "********")
                    : arguments;

                _logger.LogInformation("Chạy SteamCMD với tham số: {SafeArguments}", safeArguments);
                await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy SteamCMD cho {profile.Name}...");

                // Khởi tạo Process
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

                // Theo dõi output
                steamCmdProcess.OutputDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogInformation("SteamCMD Output: {Data}", e.Data);
                        await SafeSendLogAsync(profile.Name, "Info", e.Data);

                        // Kiểm tra yêu cầu Steam Guard - điều kiện chính xác và đầy đủ hơn
                        if (e.Data.Contains("Steam Guard code:") ||
                            e.Data.Contains("Two-factor code:") ||
                            e.Data.Contains("Steam Guard Code:") ||
                            e.Data.Contains("Enter the current code") ||
                            e.Data.Contains("Mobile Authenticator") ||
                            (e.Data.ToLower().Contains("steam guard") && !e.Data.Contains("thành công")))
                        {
                            // Xử lý tức thì 2FA trước khi ghi log thành công
                            await ProcessTwoFactorRequest(profileId, profile.Name, steamCmdProcess);
                        }
                    }
                };

                // Theo dõi lỗi
                steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogError("SteamCMD Error: {Data}", e.Data);
                        await SafeSendLogAsync(profile.Name, "Error", e.Data);
                    }
                };

                // Thêm process vào danh sách
                _steamCmdProcesses[profileId] = steamCmdProcess;

                // Bắt đầu process
                steamCmdProcess.Start();

                // Cập nhật PID
                profile.Pid = steamCmdProcess.Id;
                await _profileService.UpdateProfile(profile);

                // Bắt đầu đọc output và error
                steamCmdProcess.BeginOutputReadLine();
                steamCmdProcess.BeginErrorReadLine();

                // Đợi process kết thúc
                await steamCmdProcess.WaitForExitAsync();

                _logger.LogInformation("SteamCMD process đã kết thúc với exit code: {ExitCode}", steamCmdProcess.ExitCode);
                await SafeSendLogAsync(profile.Name, steamCmdProcess.ExitCode == 0 ? "Success" : "Error",
                    $"SteamCMD đã kết thúc với mã: {steamCmdProcess.ExitCode}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD: {ex.Message}");
                throw;
            }
            finally
            {
                // Đảm bảo xóa tiến trình khỏi danh sách
                _steamCmdProcesses.TryRemove(profileId, out _);

                // Xóa symbolic link sau khi chạy xong
                await RemoveSteamAppsSymLink();

                // Giải phóng bộ nhớ
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

        // Phương thức xử lý yêu cầu 2FA
        private async Task ProcessTwoFactorRequest(int profileId, string profileName, Process steamCmdProcess)
        {
            _logger.LogInformation("Phát hiện yêu cầu 2FA, gửi yêu cầu popup");

            try
            {
                // Gửi thông báo đến log về việc yêu cầu mã 2FA với độ ưu tiên cao hơn
                await SafeSendLogAsync(profileName, "Warning", "Steam Guard code: Vui lòng nhập mã xác thực");

                // Gửi yêu cầu trực tiếp thông qua SignalR 
                await _hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);

                // Đợi mã 2FA với thời gian ngắn hơn
                string twoFactorCode = await LogHub.RequestTwoFactorCode(profileId, _hubContext);

                // Nếu nhận được mã, gửi vào process
                if (!string.IsNullOrEmpty(twoFactorCode))
                {
                    await SafeSendLogAsync(profileName, "Info", "Đã nhận mã 2FA, đang tiếp tục...");

                    // Đảm bảo thêm ký tự xuống dòng sau mã
                    await steamCmdProcess.StandardInput.WriteLineAsync(twoFactorCode);
                    await steamCmdProcess.StandardInput.FlushAsync();

                    _logger.LogInformation("Đã gửi mã 2FA cho Steam Guard: {Code}", twoFactorCode);
                }
                else
                {
                    _logger.LogWarning("Không nhận được mã 2FA, quá trình có thể thất bại");
                    await SafeSendLogAsync(profileName, "Warning", "Không nhận được mã 2FA, quá trình có thể thất bại");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý yêu cầu 2FA");
                await SafeSendLogAsync(profileName, "Error", $"Lỗi khi xử lý 2FA: {ex.Message}");
            }
        }

        public List<LogEntry> GetLogs()
        {
            lock (_logs)
            {
                return new List<LogEntry>(_logs);
            }
        }

        /// <summary>
        /// Xóa log cũ để giải phóng bộ nhớ
        /// </summary>
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

        #region Helper Classes and Methods

        /// <summary>
        /// Đảm bảo dừng tất cả các process khi ứng dụng tắt
        /// </summary>
        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các process trước khi tắt ứng dụng...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các process trước khi tắt ứng dụng...");

            await StopAllProfilesAsync();

            // Dừng timer
            _scheduleTimer.Stop();
            _scheduleTimer.Dispose();
        }

        /// <summary>
        /// Hỗ trợ khởi động lại profile
        /// </summary>
        public async Task<bool> RestartProfileAsync(int profileId)
        {
            // Đảm bảo profile đã dừng
            await StopAndCleanupProfileAsync(profileId);

            // Đợi một chút để đảm bảo process đã dừng hoàn toàn
            await Task.Delay(2000);

            // Khởi động lại profile
            return await StartProfileAsync(profileId);
        }

        /// <summary>
        /// Khởi động tất cả các profile được đánh dấu AutoRun
        /// </summary>
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

                    // Đảm bảo dừng tiến trình cũ trước khi khởi động lại
                    await StopAndCleanupProfileAsync(profile.Id);

                    // Khởi động lại
                    await StartProfileAsync(profile.Id);

                    // Đợi một chút giữa các lần khởi động để tránh quá tải hệ thống
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

        #endregion
    }
}