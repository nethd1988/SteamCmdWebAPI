using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SteamCmdWebAPI.Services
{
    public class AccountEncryptionService : IHostedService
    {
        private readonly ILogger<AccountEncryptionService> _logger;
        private readonly IServiceProvider _serviceProvider;

        public AccountEncryptionService(
            ILogger<AccountEncryptionService> logger,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Khởi động dịch vụ mã hóa tài khoản...");
            
            try
            {
                // Tạo scope để lấy dịch vụ SteamAccountService
                using (var scope = _serviceProvider.CreateScope())
                {
                    var accountService = scope.ServiceProvider.GetRequiredService<SteamAccountService>();
                    
                    // Mã hóa lại tất cả tài khoản
                    await accountService.ReencryptAllAccountsAsync();
                    
                    _logger.LogInformation("Đã hoàn thành việc mã hóa lại tài khoản");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi khởi động dịch vụ mã hóa tài khoản: {Message}", ex.Message);
            }
            
            return;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Dừng dịch vụ mã hóa tài khoản");
            return Task.CompletedTask;
        }
    }
} 