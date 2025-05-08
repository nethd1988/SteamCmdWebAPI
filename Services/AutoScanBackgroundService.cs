using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class AutoScanBackgroundService : BackgroundService
    {
        private readonly ILogger<AutoScanBackgroundService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(10); // Kiểm tra mỗi 10 phút

        public AutoScanBackgroundService(
            ILogger<AutoScanBackgroundService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Dịch vụ quét tự động game đã khởi động");

            // Chờ 1 phút sau khi ứng dụng khởi động để đảm bảo mọi thứ đã sẵn sàng
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ScanAccountsDueForUpdate();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi trong quá trình quét tự động game");
                }

                // Đợi đến lần kiểm tra tiếp theo
                await Task.Delay(_checkInterval, stoppingToken);
            }
        }

        private async Task ScanAccountsDueForUpdate()
        {
            _logger.LogInformation("Đang kiểm tra các tài khoản cần quét tự động...");

            using (var scope = _serviceProvider.CreateScope())
            {
                var accountService = scope.ServiceProvider.GetRequiredService<SteamAccountService>();
                var steamAppInfoService = scope.ServiceProvider.GetRequiredService<SteamAppInfoService>();

                // Lấy tất cả tài khoản và lọc ra những tài khoản đến thời gian quét
                var accounts = await accountService.GetAllAccountsAsync();
                var now = DateTime.Now;
                var accountsDueForScan = accounts.Where(a => 
                    a.AutoScanEnabled && 
                    a.NextScanTime.HasValue && 
                    a.NextScanTime.Value <= now).ToList();

                _logger.LogInformation("Có {Count} tài khoản cần quét tự động", accountsDueForScan.Count);

                foreach (var account in accountsDueForScan)
                {
                    try
                    {
                        _logger.LogInformation("Đang quét tự động tài khoản {ProfileName} (ID: {Id})", account.ProfileName, account.Id);

                        if (string.IsNullOrEmpty(account.Username) || string.IsNullOrEmpty(account.Password))
                        {
                            _logger.LogWarning("Tài khoản {ProfileName} (ID: {Id}) thiếu thông tin đăng nhập, bỏ qua", account.ProfileName, account.Id);
                            continue;
                        }

                        // Lấy danh sách game hiện tại
                        var existingAppIds = !string.IsNullOrEmpty(account.AppIds)
                            ? account.AppIds.Split(',').Select(a => a.Trim()).Where(a => !string.IsNullOrEmpty(a)).ToList()
                            : new List<string>();

                        // Quét game từ tài khoản
                        var games = await steamAppInfoService.ScanAccountGames(account.Username, account.Password);

                        if (games.Count > 0)
                        {
                            // Hợp nhất danh sách game
                            var allAppIds = new HashSet<string>(existingAppIds);
                            var scannedAppIds = games.Select(g => g.AppId).ToList();
                            
                            foreach (var appId in scannedAppIds)
                            {
                                allAppIds.Add(appId);
                            }

                            int newGamesCount = allAppIds.Count - existingAppIds.Count;

                            if (newGamesCount > 0)
                            {
                                _logger.LogInformation("Tìm thấy {NewGamesCount} game mới cho tài khoản {ProfileName}", newGamesCount, account.ProfileName);

                                // Cập nhật danh sách AppIDs
                                account.AppIds = string.Join(",", allAppIds);

                                // Cập nhật danh sách tên game
                                var allGameNames = new HashSet<string>();
                                if (!string.IsNullOrEmpty(account.GameNames))
                                {
                                    var existingGameNames = account.GameNames.Split(',').Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g));
                                    foreach (var gameName in existingGameNames)
                                    {
                                        allGameNames.Add(gameName);
                                    }
                                }

                                foreach (var (_, gameName) in games)
                                {
                                    if (!string.IsNullOrEmpty(gameName))
                                    {
                                        allGameNames.Add(gameName);
                                    }
                                }

                                account.GameNames = string.Join(",", allGameNames);
                            }
                            else
                            {
                                _logger.LogInformation("Không tìm thấy game mới cho tài khoản {ProfileName}", account.ProfileName);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Không thể lấy danh sách game cho tài khoản {ProfileName}", account.ProfileName);
                        }

                        // Cập nhật thời gian quét
                        account.LastScanTime = DateTime.Now;
                        account.NextScanTime = DateTime.Now.AddHours(account.ScanIntervalHours);
                        await accountService.UpdateAccountAsync(account);

                        _logger.LogInformation("Đã hoàn thành quét tự động tài khoản {ProfileName}, lần quét tiếp theo: {NextScan}",
                            account.ProfileName, account.NextScanTime);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi quét tự động tài khoản {ProfileName} (ID: {Id})", account.ProfileName, account.Id);
                        
                        // Cập nhật thời gian quét để thử lại sau
                        account.LastScanTime = DateTime.Now;
                        account.NextScanTime = DateTime.Now.AddHours(1); // Thử lại sau 1 giờ
                        await accountService.UpdateAccountAsync(account);
                    }

                    // Chờ một chút giữa các lần quét để tránh quá tải hệ thống
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
            }

            _logger.LogInformation("Đã hoàn thành kiểm tra các tài khoản cần quét tự động");
        }
    }
} 