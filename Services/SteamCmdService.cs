using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;
using System.Net.Http;
using SteamCmdWebAPI.Helpers;
using SteamCmdWebAPI.Extensions;

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

        private const int MaxLogEntries = 5000;
        // Adjusted base retry delay and process exit timeout based on user's request for higher delays
        private const int RetryDelayMs = 5000; // Increased base retry delay
        private const int ProcessExitTimeoutMs = 20000; // Increased process exit timeout

        private readonly ConcurrentDictionary<int, Process> _steamCmdProcesses = new ConcurrentDictionary<int, Process>();
        private readonly System.Timers.Timer _scheduleTimer; // Assuming scheduling is needed based on original code

        private volatile bool _isRunningAllProfiles = false; // Assuming auto-run feature is needed
        private int _currentProfileIndex = 0; // Assuming auto-run feature is needed
        private volatile bool _cancelAutoRun = false; // Assuming auto-run feature is needed
        private DateTime _lastAutoRunTime = DateTime.MinValue; // Assuming auto-run feature is needed

        // Assuming logging is needed based on original code
        private readonly List<LogEntry> _logs = new List<LogEntry>(MaxLogEntries);
        private HashSet<string> _recentLogMessages = new HashSet<string>();
        private readonly int _maxRecentLogMessages = 100;

        // LogEntry class as in the original code
        public class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string ProfileName { get; set; }
            public string Status { get; set; }
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

            // Assuming scheduling is needed
            _scheduleTimer = new System.Timers.Timer(60000);
            _scheduleTimer.Elapsed += async (s, e) => await CheckScheduleAsync();
            _scheduleTimer.AutoReset = true;
            _scheduleTimer.Start();
            _logger.LogInformation("Bộ lập lịch đã khởi động.");
        }

        #region Log and Notification Methods
        // Keeping original logging methods
        private void AddLog(LogEntry entry)
        {
            lock (_logs)
            {
                _logs.Add(entry);
                if (_logs.Count > MaxLogEntries)
                {
                    _logs.RemoveRange(0, _logs.Count - MaxLogEntries);
                }
            }
        }

        private async Task SafeSendLogAsync(string profileName, string status, string message)
        {
            try
            {
                string logKey = $"{profileName}:{status}:{message}";

                if (_recentLogMessages.Contains(logKey))
                {
                    return;
                }

                _recentLogMessages.Add(logKey);

                if (_recentLogMessages.Count > _maxRecentLogMessages)
                {
                    _recentLogMessages.Clear();
                }

                await _hubContext.Clients.All.SendAsync("ReceiveLog", message);
                AddLog(new LogEntry(DateTime.Now, profileName, status, message));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi log: {Message}", ex.Message);
            }
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

        #region Schedule and Auto Run
        // Keeping original scheduling/auto-run methods
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

            await KillAllSteamCmdProcessesAsync();
            await Task.Delay(RetryDelayMs); // Use the adjusted RetryDelayMs

            foreach (var profile in autoRunProfiles)
            {
                try
                {
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang tự động khởi động {profile.Name}...");
                    await RunProfileAsync(profile.Id);
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

        #region Process Management Utilities
        // Keeping original process management methods with adjusted timeouts
        private async Task<bool> KillProcessAsync(Process process, string profileName)
        {
            if (process == null || process.HasExited)
                return true;

            try
            {
                _logger.LogInformation("Đang dừng tiến trình SteamCMD cho profile {ProfileName}, PID: {Pid}",
                    profileName, process.Id);

                await SafeSendLogAsync(profileName, "Info", $"Đang dừng tiến trình SteamCMD (PID: {process.Id})");

                // Use adjusted ProcessExitTimeoutMs
                process.Terminator(ProcessExitTimeoutMs);

                await SafeSendLogAsync(profileName, "Info", $"Đã dừng tiến trình SteamCMD (PID: {process.Id})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD cho profile {ProfileName}", profileName);

                try
                {
                    // Use CmdHelper with adjusted timeout for taskkill
                    CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe", ProcessExitTimeoutMs);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private async Task<bool> KillAllSteamCmdProcessesAsync()
        {
            try
            {
                // Stop all tracked processes
                foreach (var kvp in _steamCmdProcesses.ToArray())
                {
                    await KillProcessAsync(kvp.Value, $"Profile {kvp.Key}");
                    _steamCmdProcesses.TryRemove(kvp.Key, out _);
                }

                // Check and stop any remaining SteamCMD processes
                var processes = Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")).ToList();

                if (processes.Any())
                {
                    _logger.LogInformation("Đang dừng {Count} tiến trình SteamCMD...", processes.Count);

                    foreach (var p in processes)
                    {
                        try
                        {
                            if (!p.HasExited)
                            {
                                p.Terminator(ProcessExitTimeoutMs); // Use adjusted timeout
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Lỗi khi dừng tiến trình SteamCMD, PID: {Pid}", p.Id);
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }

                    await Task.Delay(RetryDelayMs); // Use adjusted retry delay
                    processes = Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")).ToList();

                    if (processes.Any())
                    {
                        // Use CmdHelper with adjusted timeout for taskkill
                        CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe", ProcessExitTimeoutMs);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi dừng tất cả tiến trình SteamCMD");
                return false;
            }
        }

        #endregion

        #region SteamCMD Installation and Path Management
        // Keeping original installation and path management methods
        private string GetSteamCmdPath()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            return Path.Combine(steamCmdDir, "steamcmd.exe");
        }

        public Task<bool> IsSteamCmdInstalled()
        {
            string steamCmdPath = GetSteamCmdPath();
            bool exists = File.Exists(steamCmdPath);
            _logger.LogInformation("Kiểm tra SteamCMD tại {Path}: {Exists}", steamCmdPath, exists);
            return Task.FromResult(exists);
        }

        public async Task InstallSteamCmd()
        {
            string steamCmdDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd");
            string zipPath = Path.Combine(steamCmdDir, "steamcmd.zip");
            string downloadUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

            try
            {
                if (!Directory.Exists(steamCmdDir))
                {
                    Directory.CreateDirectory(steamCmdDir);
                    _logger.LogInformation("Đã tạo thư mục steamcmd: {Directory}", steamCmdDir);
                    await SafeSendLogAsync("System", "Info", $"Đã tạo thư mục steamcmd: {steamCmdDir}");
                }

                using (var httpClient = new HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromMinutes(5);
                    await SafeSendLogAsync("System", "Info", $"Đang tải SteamCMD từ {downloadUrl}...");
                    _logger.LogInformation("Bắt đầu tải SteamCMD từ {Url}", downloadUrl);
                    var response = await httpClient.GetAsync(downloadUrl);
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(zipPath, FileMode.Create))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                    await SafeSendLogAsync("System", "Info", "Đã tải xong SteamCMD, đang giải nén...");
                }

                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, steamCmdDir, true);
                await SafeSendLogAsync("System", "Success", "Đã cài đặt SteamCMD thành công");

                try { File.Delete(zipPath); }
                catch (Exception ex) { _logger.LogWarning(ex, "Không thể xóa file zip"); }

                if (!File.Exists(GetSteamCmdPath()))
                {
                    throw new Exception("Cài đặt thất bại. Không tìm thấy steamcmd.exe sau khi cài đặt.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi trong quá trình cài đặt SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync("System", "Error", $"Lỗi khi cài đặt SteamCMD: {ex.Message}");
                throw;
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
            return Path.Combine(logsDir, $"console_log_{profileId}.txt");
        }

        #endregion

        #region Folder Setup

        /// <summary>
        /// Prepares the folder structure, including deleting the old local steamapps directory and creating a symbolic link.
        /// This method ensures the local steamapps directory is deleted (with retries and process killing) before creating the symbolic link.
        /// </summary>
        /// <param name="gameInstallDir">The game installation directory where the target steamapps folder resides.</param>
        /// <returns>True if the folder structure preparation (deletion and symbolic link creation) was successful, false otherwise.</returns>
        private async Task<bool> PrepareFolderStructure(string gameInstallDir)
        {
            string steamappsDir = Path.Combine(gameInstallDir, "steamapps");
            string localSteamappsDir = Path.Combine(Directory.GetCurrentDirectory(), "steamcmd", "steamapps");

            // Ensure the game install steamapps directory exists as the target for the symbolic link
            if (!Directory.Exists(steamappsDir))
            {
                try
                {
                    Directory.CreateDirectory(steamappsDir);
                    _logger.LogInformation($"Created game install steamapps directory: {steamappsDir}");
                    await SafeSendLogAsync("System", "Info", $"Created game install steamapps directory: {steamappsDir}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to create game install steamapps directory: {steamappsDir}");
                    await SafeSendLogAsync("System", "Error", $"Failed to create game install steamapps directory: {steamappsDir}: {ex.Message}");
                    // If the target directory cannot be created, we cannot create the symbolic link later.
                    return false;
                }
            }

            // Delete the existing local steamapps folder if it exists - Ensure deletion with retries and kill
            if (Directory.Exists(localSteamappsDir))
            {
                _logger.LogInformation($"Attempting to delete old local steamapps directory: {localSteamappsDir}");
                await SafeSendLogAsync("System", "Info", $"Attempting to delete old local steamapps directory: {localSteamappsDir}");

                bool deleted = false;
                int retryCount = 15; // Increased max retry attempts for robustness
                int baseRetryDelayMs = 3000; // Increased base delay between retries

                for (int i = 0; i < retryCount; i++)
                {
                    try
                    {
                        _logger.LogInformation($"Attempting to delete directory {localSteamappsDir} attempt {i + 1}/{retryCount}...");
                        await SafeSendLogAsync("System", "Info", $"Attempting to delete old steamapps directory attempt {i + 1}/{retryCount}...");

                        // Use rmdir /S /Q to force delete the directory and its contents
                        // Increased timeout for deletion command to allow more time
                        CmdHelper.RunCommand($"rmdir /S /Q \"{localSteamappsDir}\"", 45000); // Increased timeout
                        await Task.Delay(baseRetryDelayMs); // Short delay after running the command

                        if (!Directory.Exists(localSteamappsDir))
                        {
                            _logger.LogInformation("Successfully deleted old local steamapps directory.");
                            await SafeSendLogAsync("System", "Success", "Successfully deleted old local steamapps directory.");
                            deleted = true;
                            break; // Deletion successful, exit loop
                        }
                        else
                        {
                            _logger.LogWarning($"Old local steamapps directory still exists after attempt {i + 1}. Attempting to kill SteamCMD and waiting before retrying.");
                            await SafeSendLogAsync("System", "Warning", $"Old local steamapps directory still exists after attempt {i + 1}. Attempting to kill SteamCMD and waiting before retrying.");

                            // Attempt to kill SteamCMD processes if deletion fails
                            await KillAllSteamCmdProcessesAsync();
                            await Task.Delay(7000); // Increased wait time (7 seconds) after killing before retrying deletion

                            await Task.Delay(baseRetryDelayMs * (i + 1)); // Increase delay for subsequent retries
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error deleting old local steamapps directory on attempt {i + 1}");
                        await SafeSendLogAsync("System", "Error", $"Error deleting old local steamapps directory on attempt {i + 1}: {ex.Message}");

                        // Attempt to kill SteamCMD processes if deletion encounters an error
                        _logger.LogInformation($"Encountered error during directory deletion, attempting to kill SteamCMD before retrying.");
                        await SafeSendLogAsync("System", "Info", $"Encountered error during directory deletion, attempting to kill SteamCMD before retrying.");
                        await KillAllSteamCmdProcessesAsync();
                        await Task.Delay(7000); // Increased wait time (7 seconds) after killing before retrying deletion

                        await Task.Delay(baseRetryDelayMs * (i + 1)); // Wait before next retry
                    }
                }

                if (!deleted)
                {
                    _logger.LogError($"Failed to delete old local steamapps directory at {localSteamappsDir} after {retryCount} attempts. Cannot proceed with symbolic link.");
                    await SafeSendLogAsync("System", "Error", $"Failed to delete old local steamapps directory at {localSteamappsDir} after {retryCount} attempts. Cannot proceed with symbolic link.");
                    // If deletion failed after all retries, we cannot proceed with the symbolic link
                    return false; // Indicate failure
                }
            }

            // Create symbolic link only if the old local steamapps directory was successfully deleted (or didn't exist initially)
            // and the target directory exists.
            if (!Directory.Exists(localSteamappsDir))
            {
                _logger.LogInformation($"Creating symbolic link from \"{steamappsDir}\" to \"{localSteamappsDir}\"");
                await SafeSendLogAsync("System", "Info", $"Creating symbolic link from \"{steamappsDir}\" to \"{localSteamappsDir}\"");
                try
                {
                    // Ensure the target directory exists before creating the link (already checked above, but defensive check)
                    if (!Directory.Exists(steamappsDir))
                    {
                        _logger.LogError($"Target directory for symbolic link does not exist: {steamappsDir}. Cannot create link.");
                        await SafeSendLogAsync("System", "Error", $"Target directory for symbolic link does not exist: {steamappsDir}. Cannot create link.");
                        return false; // Cannot create link if target is missing
                    }

                    // Use mklink /D to create a directory symbolic link
                    CmdHelper.RunCommand($"mklink /D \"{localSteamappsDir}\" \"{steamappsDir}\"", 15000); // Increased timeout for mklink command
                    await Task.Delay(3000); // Increased delay after creating link

                    if (Directory.Exists(localSteamappsDir))
                    {
                        _logger.LogInformation("Successfully created symbolic link.");
                        await SafeSendLogAsync("System", "Success", "Successfully created symbolic link.");
                        return true; // Symbolic link created successfully
                    }
                    else
                    {
                        _logger.LogError($"Failed to create symbolic link to {localSteamappsDir}. Directory does not exist after mklink command.");
                        await SafeSendLogAsync("System", "Error", $"Failed to create symbolic link to {localSteamappsDir}. Directory does not exist after mklink command.");
                        return false; // Symbolic link creation failed
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error creating symbolic link");
                    await SafeSendLogAsync("System", "Error", $"Error creating symbolic link: {ex.Message}");
                    return false; // Error during symbolic link creation
                }
            }
            else
            {
                // This case indicates that Directory.Exists(localSteamappsDir) was true initially
                // but the deletion failed after all retries.
                _logger.LogError("Old local steamapps directory still exists after deletion attempts. Skipping symbolic link creation.");
                await SafeSendLogAsync("System", "Error", "Old local steamapps directory still exists after deletion attempts. Skipping symbolic link creation.");
                return false; // Deletion failed, so symbolic link wasn't created
            }
        }

        #endregion

        #region Public API Methods

        /// <summary>
        /// Runs a SteamCMD profile, including process cleanup, installation check,
        /// initial SteamCMD setup (if needed), folder structure preparation (delete steamapps and create symbolic link),
        /// and running the main SteamCMD process for the profile.
        /// Ensures SteamCMD is ready and folder preparation is successful before running the main process.
        /// </summary>
        /// <param name="id">The ID of the profile to run.</param>
        /// <returns>True if the profile ran successfully, false otherwise.</returns>
        public async Task<bool> RunProfileAsync(int id)
        {
            try
            {
                var profile = await _profileService.GetProfileById(id);
                if (profile == null)
                {
                    _logger.LogError("Profile ID {ProfileId} not found", id);
                    await SafeSendLogAsync($"Profile {id}", "Error", $"Profile with ID {id} not found");
                    return false;
                }

                await SafeSendLogAsync(profile.Name, "Info", $"Preparing to run {profile.Name}...");

                // Step 1: Clean up environment before running
                // Kill any existing process for this profile
                if (_steamCmdProcesses.TryGetValue(id, out var existingProcess))
                {
                    await KillProcessAsync(existingProcess, profile.Name);
                    _steamCmdProcesses.TryRemove(id, out _);
                }

                // Kill any other lingering SteamCMD processes to ensure a clean state
                await KillAllSteamCmdProcessesAsync();

                // Wait after attempting to kill all SteamCMD processes for stability
                _logger.LogInformation("Waiting 20 seconds after attempting to stop all SteamCMD processes...");
                await SafeSendLogAsync("System", "Info", "Waiting 20 seconds after attempting to stop all SteamCMD processes...");
                await Task.Delay(20000); // Increased delay to 20 seconds

                // Optional: Add a check here to confirm no steamcmd.exe processes are running if desired
                // This adds an extra layer of certainty but might add more time.
                // var remainingProcesses = Process.GetProcessesByName("steamcmd").Union(Process.GetProcessesByName("steamcmd.exe")).ToList();
                // if (remainingProcesses.Any()) {
                //     _logger.LogWarning($"Found {remainingProcesses.Count} steamcmd.exe processes still running after waiting. Attempting forceful kill again.");
                //     await SafeSendLogAsync("System", "Warning", $"Found {remainingProcesses.Count} steamcmd.exe processes still running after waiting. Attempting forceful kill again.");
                //     CmdHelper.RunCommand("taskkill /F /IM steamcmd.exe", ProcessExitTimeoutMs);
                //     await Task.Delay(5000); // Wait again after forceful kill
                // }


                // Step 2: Check and install SteamCMD, perform initial self-setup if needed
                bool steamCmdFileExists = await IsSteamCmdInstalled();

                if (!steamCmdFileExists)
                {
                    await SafeSendLogAsync(profile.Name, "Info", "SteamCMD is not installed. Downloading...");
                    await InstallSteamCmd(); // Waits for zip download and extraction

                    // After file installation, check if the executable now exists
                    steamCmdFileExists = await IsSteamCmdInstalled();

                    if (!steamCmdFileExists)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", "Failed to install SteamCMD executable.");
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        return false; // Cannot proceed if steamcmd.exe file is not there
                    }
                    else
                    {
                        // SteamCMD executable file is now present, perform initial run for self-setup
                        _logger.LogInformation("SteamCMD executable installed. Performing initial self-setup run...");
                        await SafeSendLogAsync(profile.Name, "Info", "SteamCMD executable installed. Performing initial self-setup run...");

                        // --- NEW STEP: Initial SteamCMD self-setup run ---
                        try
                        {
                            string steamCmdPath = GetSteamCmdPath();
                            // Run steamcmd with +quit to trigger initial updates/setup
                            ProcessStartInfo startInfo = new ProcessStartInfo
                            {
                                FileName = steamCmdPath,
                                Arguments = "+quit", // Simple command to make it start and exit
                                UseShellExecute = false,
                                RedirectStandardOutput = true, // Capture output to potentially check for "Loading Steam API...OK"
                                RedirectStandardError = true,
                                CreateNoWindow = true,
                                WorkingDirectory = Path.GetDirectoryName(steamCmdPath)
                            };

                            using (Process initialProcess = new Process { StartInfo = startInfo })
                            {
                                _logger.LogInformation("Starting initial SteamCMD self-setup process...");
                                await SafeSendLogAsync(profile.Name, "Info", "Starting initial SteamCMD self-setup process...");

                                initialProcess.Start();

                                // Optional: Read and log output during initial setup if needed for debugging
                                // initialProcess.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogInformation($"Initial SteamCMD Output: {e.Data}"); };
                                // initialProcess.ErrorDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogError($"Initial SteamCMD Error: {e.Data}"); };
                                // initialProcess.BeginOutputReadLine();
                                // initialProcess.BeginErrorReadLine();

                                // Wait for the initial setup process to exit
                                // Use a reasonable timeout for this initial setup
                                bool exited = initialProcess.WaitForExit(120000); // Wait up to 120 seconds (2 minutes)

                                if (!exited)
                                {
                                    _logger.LogError("Initial SteamCMD self-setup timed out.");
                                    await SafeSendLogAsync(profile.Name, "Error", "Initial SteamCMD self-setup timed out.");
                                    // Attempt to kill the hung process
                                    try { initialProcess.Kill(); } catch { }
                                    profile.Status = "Stopped";
                                    profile.StopTime = DateTime.Now;
                                    await _profileService.UpdateProfile(profile);
                                    return false; // Fail if initial setup times out
                                }

                                if (initialProcess.ExitCode == 0)
                                {
                                    _logger.LogInformation("Initial SteamCMD self-setup completed successfully.");
                                    await SafeSendLogAsync(profile.Name, "Success", "Initial SteamCMD self-setup completed successfully.");
                                }
                                else
                                {
                                    _logger.LogError($"Initial SteamCMD self-setup failed with exit code {initialProcess.ExitCode}.");
                                    await SafeSendLogAsync(profile.Name, "Error", $"Initial SteamCMD self-setup failed with exit code {initialProcess.ExitCode}.");
                                    profile.Status = "Stopped";
                                    profile.StopTime = DateTime.Now;
                                    await _profileService.UpdateProfile(profile);
                                    return false; // Fail if initial setup returns non-zero exit code
                                }
                            }
                            // --- END NEW STEP ---
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error during initial SteamCMD self-setup run.");
                            await SafeSendLogAsync(profile.Name, "Error", $"Error during initial SteamCMD self-setup run: {ex.Message}");
                            profile.Status = "Stopped";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                            return false; // Fail if an exception occurs during initial setup run
                        }
                    }
                }
                else
                {
                    // SteamCMD was already installed, log this and continue
                    _logger.LogInformation("SteamCMD is already installed.");
                    await SafeSendLogAsync(profile.Name, "Info", "SteamCMD đã có sẵn.");
                    // If already installed, we assume initial setup was done previously.
                }


                // Step 3: Check and prepare installation directory (Proceed only if Step 2 was successful)
                if (!string.IsNullOrEmpty(profile.InstallDirectory))
                {
                    if (!Directory.Exists(profile.InstallDirectory))
                    {
                        try
                        {
                            Directory.CreateDirectory(profile.InstallDirectory);
                            await SafeSendLogAsync(profile.Name, "Info", $"Created installation directory: {profile.InstallDirectory}");
                        }
                        catch (Exception ex)
                        {
                            await SafeSendLogAsync(profile.Name, "Error", $"Failed to create installation directory: {ex.Message}");
                            profile.Status = "Stopped";
                            profile.StopTime = DateTime.Now;
                            await _profileService.UpdateProfile(profile);
                            return false;
                        }
                    }

                    // Check write permissions for the installation directory
                    try
                    {
                        string testFilePath = Path.Combine(profile.InstallDirectory, "writetest.tmp");
                        File.WriteAllText(testFilePath, "test");
                        File.Delete(testFilePath);
                    }
                    catch (Exception ex)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"No write permissions for installation directory: {ex.Message}");
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        await _profileService.UpdateProfile(profile);
                        return false;
                    }
                }

                // Step 4: Prepare folder structure (Delete steamapps and create symbolic link)
                // Crucially, check if folder preparation was successful before proceeding to run SteamCMD.
                bool folderPreparationSuccess = await PrepareFolderStructure(profile.InstallDirectory);

                if (!folderPreparationSuccess)
                {
                    _logger.LogError($"Folder structure preparation failed for profile {profile.Name}. Aborting run.");
                    await SafeSendLogAsync(profile.Name, "Error", "Folder structure preparation failed. Aborting run.");
                    // Update profile status to reflect the failure in folder preparation
                    profile.Status = "Stopped";
                    profile.StopTime = DateTime.Now;
                    profile.Pid = 0; // Ensure PID is reset
                    await _profileService.UpdateProfile(profile);
                    return false; // Stop execution if folder preparation failed
                }

                // Step 5: Run SteamCMD process for the profile (ONLY if Step 2 and Step 4 were successful)
                // This ensures steamcmd.exe runs only after initial setup is done, old steamapps is removed, and link is ready.
                bool success = await RunSteamCmdProcessAsync(profile, id);

                // Update profile status after attempting to run SteamCMD
                profile.Status = success ? "Stopped" : "Error"; // Set status based on RunSteamCmdProcessAsync result
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                profile.LastRun = DateTime.UtcNow; // Update last run time regardless of success
                await _profileService.UpdateProfile(profile);

                if (success)
                {
                    await SafeSendLogAsync(profile.Name, "Success", $"Successfully ran {profile.Name}");
                }
                else
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"Failed to run {profile.Name}. Check logs for details.");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running profile {ProfileId}: {Message}", id, ex.Message);

                // Handle unexpected errors during the profile run
                try
                {
                    var profile = await _profileService.GetProfileById(id);
                    if (profile != null)
                    {
                        await SafeSendLogAsync(profile.Name, "Error", $"An unexpected error occurred while running {profile.Name}: {ex.Message}");
                        profile.Status = "Stopped";
                        profile.StopTime = DateTime.Now;
                        profile.Pid = 0;
                        await _profileService.UpdateProfile(profile);
                    }
                }
                catch { /* Ignore errors during error logging/profile update */ }

                // Ensure the process is removed from tracking if an error occurred
                _steamCmdProcesses.TryRemove(id, out _);
                return false;
            }
        }

        /// <summary>
        /// Runs the actual SteamCMD process for a given profile.
        /// </summary>
        /// <param name="profile">The SteamCmdProfile to run.</param>
        /// <param name="profileId">The ID of the profile.</param>
        /// <returns>True if the SteamCMD process finished successfully, false otherwise.</returns>
        private async Task<bool> RunSteamCmdProcessAsync(SteamCmdProfile profile, int profileId)
        {
            Process steamCmdProcess = null;
            bool success = false;
            string steamCmdPath = GetSteamCmdPath();
            // Using HashSet for efficient checking of success messages
            var successMessages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                $"Success! App '{profile.AppID}' already up to date.",
                $"Success! App '{profile.AppID}' fully installed.",
                $"Success! App '{profile.AppID}' updated."
            };


            try
            {
                if (!File.Exists(steamCmdPath))
                {
                    await SafeSendLogAsync(profile.Name, "Error", $"File SteamCMD không tồn tại tại {steamCmdPath}");
                    return false;
                }

                string logFilePath = GetSteamCmdLogPath(profileId);

                // Start LogFileReader to monitor the console log file
                _logFileReader.StartMonitoring(logFilePath, async (content) =>
                {
                    if (!string.IsNullOrEmpty(content))
                    {
                        // Send log content to clients via SignalR
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", content);
                    }
                });

                try
                {
                    // Prepare login command
                    string loginCommand = "+login anonymous";
                    if (!string.IsNullOrEmpty(profile.SteamUsername) && !string.IsNullOrEmpty(profile.SteamPassword))
                    {
                        try
                        {
                            string username = _encryptionService.Decrypt(profile.SteamUsername);
                            string password = _encryptionService.Decrypt(profile.SteamPassword);
                            loginCommand = $"+login {username} {password}";
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi giải mã thông tin đăng nhập cho profile {ProfileName}", profile.Name);
                            await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi giải mã thông tin đăng nhập: {ex.Message}");
                            // Decide if this should stop the process - currently logs and continues
                        }
                    }

                    // Prepare arguments
                    string validateArg = profile.ValidateFiles ? "validate" : "";
                    string argumentsArg = string.IsNullOrEmpty(profile.Arguments) ? "" : profile.Arguments.Trim();

                    // Construct the full command arguments
                    // Ensure +quit is the last command to exit SteamCMD automatically
                    string arguments = $"{loginCommand} +app_update {profile.AppID} {validateArg} {argumentsArg} +quit".Trim();

                    // Hide sensitive information in logs
                    string safeArguments = arguments.Contains("+login ") ?
                        arguments.Replace("+login ", "+login [credentials] ") :
                        arguments;

                    _logger.LogInformation("Chạy SteamCMD với tham số: {SafeArguments}", safeArguments);
                    await SafeSendLogAsync(profile.Name, "Info", $"Đang chạy SteamCMD cho {profile.Name}...");

                    // Create and configure the SteamCMD process
                    steamCmdProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = steamCmdPath,
                            Arguments = arguments,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            RedirectStandardInput = true, // Needed for some SteamCMD interactions, though +quit is used here
                            CreateNoWindow = true, // Hide the console window
                            StandardOutputEncoding = Encoding.UTF8,
                            StandardErrorEncoding = Encoding.UTF8,
                            WorkingDirectory = Path.GetDirectoryName(steamCmdPath) // Set working directory to steamcmd folder
                        },
                        EnableRaisingEvents = true // Enable events like Exited
                    };

                    // Buffer and timer for sending output in batches to avoid flooding
                    var outputBuffer = new StringBuilder();
                    var lastMessageHash = new HashSet<string>(StringComparer.Ordinal); // Track recent messages to avoid excessive logging of duplicates
                    var duplicateCount = 0;
                    var outputTimer = new System.Timers.Timer(200); // Increased timer interval slightly

                    outputTimer.Elapsed += async (sender, e) =>
                    {
                        string output;
                        lock (outputBuffer)
                        {
                            if (outputBuffer.Length == 0) return;
                            output = outputBuffer.ToString();
                            outputBuffer.Clear();
                        }

                        // Append duplicate count info if any were skipped
                        if (duplicateCount > 0)
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"{output}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                            duplicateCount = 0; // Reset count after sending
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", output);
                        }
                    };
                    outputTimer.Start();

                    // Event handler for standard output
                    steamCmdProcess.OutputDataReceived += (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        // Check for success messages in the output
                        if (successMessages.Contains(e.Data.Trim()))
                        {
                            // Add success messages to a set to confirm success later
                            // Using a separate set for successful completion check
                            // This ensures we don't miss the final success message
                            // due to the output buffer or duplicate filtering.
                            lock (successMessages) // Protect shared resource
                            {
                                successMessages.Add(e.Data.Trim()); // Add to the set used for the final success check
                            }
                        }

                        // Duplicate message filtering for display logs
                        if (lastMessageHash.Contains(e.Data.Trim()))
                        {
                            duplicateCount++;
                            return;
                        }

                        lastMessageHash.Add(e.Data.Trim());

                        // Keep the hash set size in check
                        if (lastMessageHash.Count > 100) // Increased size limit
                        {
                            lastMessageHash.Clear();
                        }

                        // Append to the output buffer for batch sending
                        lock (outputBuffer)
                        {
                            outputBuffer.AppendLine(e.Data);
                        }
                    };

                    // Event handler for standard error
                    steamCmdProcess.ErrorDataReceived += async (sender, e) =>
                    {
                        if (string.IsNullOrEmpty(e.Data)) return;

                        // Check if this error message has been logged recently
                        if (!lastMessageHash.Contains(e.Data.Trim()))
                        {
                            _logger.LogError("SteamCMD Error: {Data}", e.Data);
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Lỗi: {e.Data}");

                            lastMessageHash.Add(e.Data.Trim());
                            if (lastMessageHash.Count > 100)
                            {
                                lastMessageHash.Clear();
                            }
                        }
                    };

                    // Add the process to the tracking dictionary
                    _steamCmdProcesses[profileId] = steamCmdProcess;

                    // Start the process
                    steamCmdProcess.Start();

                    // Update profile with PID
                    profile.Pid = steamCmdProcess.Id;
                    await _profileService.UpdateProfile(profile);

                    // Begin asynchronous reading of output and error streams
                    steamCmdProcess.BeginOutputReadLine();
                    steamCmdProcess.BeginErrorReadLine();

                    // Wait for the process to exit
                    steamCmdProcess.WaitForExit();

                    // Stop the output timer and process any remaining buffered output
                    outputTimer.Stop();
                    string remainingOutput;
                    lock (outputBuffer)
                    {
                        remainingOutput = outputBuffer.ToString();
                        outputBuffer.Clear();
                    }

                    if (!string.IsNullOrEmpty(remainingOutput))
                    {
                        if (duplicateCount > 0)
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog",
                               $"{remainingOutput}\n[{duplicateCount} thông báo trùng lặp được bỏ qua]");
                        }
                        else
                        {
                            await _hubContext.Clients.All.SendAsync("ReceiveLog", remainingOutput);
                        }
                    }

                    // Determine success based on the exit code and presence of success messages
                    // SteamCMD typically returns 0 on success, but checking output messages is more reliable for update status.
                    bool foundSuccessMessage = false;
                    lock (successMessages) // Protect shared resource when checking the set
                    {
                        // Check if any of the expected success messages were found in the output
                        foundSuccessMessage = successMessages.Any(msg => remainingOutput.Contains(msg, StringComparison.OrdinalIgnoreCase));
                        // Also check if the set of success messages itself contains any indicating completion
                        if (!foundSuccessMessage)
                        {
                            // This check might be redundant if the output handler correctly populates the set
                            // but serves as a fallback.
                            foundSuccessMessage = successMessages.Count(msg => !msg.Contains("already up to date")) > 0;
                        }
                    }


                    if (steamCmdProcess.ExitCode == 0 && foundSuccessMessage)
                    {
                        success = true;
                        string exitMessage = $"Cập nhật game {profile.Name} hoàn tất thành công (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Success", exitMessage));
                    }
                    // Consider exit code 2 as potentially successful if an update was not needed
                    else if (steamCmdProcess.ExitCode == 2 && successMessages.Any(msg => msg.Contains("already up to date", StringComparison.OrdinalIgnoreCase)))
                    {
                        success = true;
                        string exitMessage = $"Cập nhật game {profile.Name} hoàn tất: Đã cập nhật mới nhất (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Success", exitMessage));
                    }
                    else
                    {
                        success = false;
                        string exitMessage = $"Quá trình cập nhật game {profile.Name} không thành công (Exit Code: {steamCmdProcess.ExitCode}).";
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", exitMessage);
                        AddLog(new LogEntry(DateTime.Now, profile.Name, "Error", exitMessage));
                    }
                }
                finally
                {
                    // Stop monitoring the log file
                    _logFileReader.StopMonitoring();

                    // Remove the process from tracking
                    _steamCmdProcesses.TryRemove(profileId, out _);

                    // Dispose of the process object
                    if (steamCmdProcess != null)
                    {
                        if (!steamCmdProcess.HasExited)
                        {
                            try
                            {
                                // Attempt to terminate if it hasn't exited for some reason
                                steamCmdProcess.Terminator(ProcessExitTimeoutMs);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error terminating SteamCMD process in finally block: {Message}", ex.Message);
                            }
                        }
                        steamCmdProcess.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy SteamCMD: {Message}", ex.Message);
                await SafeSendLogAsync(profile.Name, "Error", $"Lỗi khi chạy SteamCMD: {ex.Message}");
                success = false;

                // Ensure process is disposed even if an exception occurs early
                steamCmdProcess?.Dispose();
            }

            return success;
        }


        // Keeping original methods for running all profiles, stopping all profiles, restarting profile, and shutdown
        public async Task RunAllProfilesAsync()
        {
            if (_isRunningAllProfiles)
            {
                await SafeSendLogAsync("System", "Warning", "Đang có quá trình chạy tất cả profile, không thể thực hiện yêu cầu này");
                return;
            }

            try
            {
                var profiles = await _profileService.GetAllProfiles();
                if (!profiles.Any())
                {
                    _logger.LogWarning("Không có cấu hình nào để chạy");
                    await SafeSendLogAsync("System", "Warning", "Không có cấu hình nào để chạy");
                    return;
                }

                _isRunningAllProfiles = true;
                _cancelAutoRun = false;
                _currentProfileIndex = 0;

                await SafeSendLogAsync("System", "Info", "Bắt đầu chạy tất cả các profile...");

                // Ensure a clean environment before starting all profiles
                await KillAllSteamCmdProcessesAsync();
                await Task.Delay(RetryDelayMs * 2); // Increased delay

                // Run each profile sequentially
                foreach (var profile in profiles)
                {
                    if (_cancelAutoRun) break; // Allow cancellation

                    _currentProfileIndex++;

                    await SafeSendLogAsync("System", "Info",
                        $"Đang chuẩn bị chạy profile ({_currentProfileIndex}/{profiles.Count}): {profile.Name}");

                    try
                    {
                        // RunProfileAsync now handles its own cleanup and status updates
                        bool success = await RunProfileAsync(profile.Id);

                        if (!success)
                        {
                            await SafeSendLogAsync(profile.Name, "Warning",
                                $"Chạy profile {profile.Name} không thành công");
                        }

                        // Wait between running profiles, unless it's the last one or cancelled
                        if (_currentProfileIndex < profiles.Count && !_cancelAutoRun)
                        {
                            await Task.Delay(5000); // Increased delay between profiles
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi chạy profile {ProfileName}", profile.Name);
                        await SafeSendLogAsync(profile.Name, "Error", $"Lỗi: {ex.Message}");
                        // Continue to the next profile even if one fails
                    }
                }

                await SafeSendLogAsync("System", "Success", "Đã hoàn thành chạy tất cả các profile");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi chạy tất cả các profile");
                await SafeSendLogAsync("System", "Error", $"Lỗi khi chạy tất cả các profile: {ex.Message}");
            }
            finally
            {
                _isRunningAllProfiles = false;
                _cancelAutoRun = false; // Reset cancel flag
            }
        }

        public async Task StopAllProfilesAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các profiles...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các profiles...");

            _cancelAutoRun = true; // Signal auto-run to stop

            // Stop all tracked processes
            foreach (var profileId in _steamCmdProcesses.Keys.ToList())
            {
                if (_steamCmdProcesses.TryRemove(profileId, out var process))
                {
                    await KillProcessAsync(process, $"Profile {profileId}");
                    process.Dispose();
                }
            }

            // Ensure no processes are left running by forcefully killing any remaining steamcmd.exe
            await KillAllSteamCmdProcessesAsync();
            await Task.Delay(RetryDelayMs); // Wait after forceful kill

            // Update status of any profile that was still marked as Running
            var profiles = await _profileService.GetAllProfiles();
            foreach (var profile in profiles.Where(p => p.Status == "Running"))
            {
                profile.Status = "Stopped";
                profile.StopTime = DateTime.Now;
                profile.Pid = 0;
                await _profileService.UpdateProfile(profile);
            }

            await SafeSendLogAsync("System", "Success", "Đã dừng tất cả các profiles");
        }

        public async Task<bool> RestartProfileAsync(int profileId)
        {
            // Stop the profile if it's running
            if (_steamCmdProcesses.TryRemove(profileId, out var process))
            {
                await KillProcessAsync(process, $"Profile {profileId}");
                process.Dispose();
            }

            await Task.Delay(RetryDelayMs * 2); // Increased delay before restarting

            // Run the profile again
            return await RunProfileAsync(profileId);
        }

        public async Task ShutdownAsync()
        {
            _logger.LogInformation("Đang dừng tất cả các process trước khi tắt ứng dụng...");
            await SafeSendLogAsync("System", "Info", "Đang dừng tất cả các process trước khi tắt ứng dụng...");

            // Stop the schedule timer
            if (_scheduleTimer != null)
            {
                _scheduleTimer.Stop();
                _scheduleTimer.Dispose();
            }


            // Stop all running profiles
            await StopAllProfilesAsync();

            _logger.LogInformation("Đã hoàn thành dừng process.");
            await SafeSendLogAsync("System", "Success", "Đã hoàn thành dừng process.");
        }
    }
}
#endregion