using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using SteamCmdWebAPI.Hubs;
using Microsoft.Extensions.Logging;

namespace SteamCmdWebAPI.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSteamCmdServices(this IServiceCollection services)
        {
            // Đăng ký các service theo thứ tự phụ thuộc
            services.AddSingleton<LicenseService>();
            services.AddSingleton<EncryptionService>();
            services.AddSingleton<SettingsService>();
            
            // Đăng ký SteamAccountService trước ProfileService
            services.AddSingleton<SteamAccountService>();
            
            // Đăng ký ProfileService với serviceProvider
            services.AddSingleton<ProfileService>(sp => new ProfileService(
                sp.GetRequiredService<ILogger<ProfileService>>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<EncryptionService>(),
                sp.GetRequiredService<LicenseService>(),
                sp // Truyền IServiceProvider
            ));
            
            services.AddSingleton<ServerSettingsService>();
            services.AddSingleton<LogFileReader>();
            services.AddSingleton<SteamApiService>();
            services.AddSingleton<LogService>();
            services.AddSingleton<DependencyManagerService>();

            // Đảm bảo QueueService được đăng ký TRƯỚC SteamCmdService
            services.AddSingleton<QueueService>();

            // Đăng ký SteamCmdService với IServiceProvider
            services.AddSingleton<SteamCmdService>(sp => new SteamCmdService(
                sp.GetRequiredService<ILogger<SteamCmdService>>(),
                sp.GetRequiredService<IHubContext<LogHub>>(),
                sp.GetRequiredService<ProfileService>(),
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<EncryptionService>(),
                sp.GetRequiredService<LogFileReader>(),
                sp.GetRequiredService<SteamApiService>(),
                sp.GetRequiredService<DependencyManagerService>(),
                sp.GetRequiredService<LogService>(),
                sp.GetRequiredService<LicenseService>(),
                sp // Truyền IServiceProvider
            ));

            services.AddSingleton<TcpClientService>();
            services.AddSingleton<UserService>();
            
            // Xóa đăng ký ở đây vì đã đăng ký ở trên
            // services.AddSingleton<SteamAccountService>();

            // Cấu hình AutoRun và UpdateCheck
            services.AddSingleton<AutoRunConfiguration>();
            services.AddHostedService<AutoRunBackgroundService>();
            services.AddHostedService<UpdateCheckService>();

            // Thêm HeartbeatService
            services.AddHostedService<HeartbeatService>();

            // Đăng ký dịch vụ Worker
            services.AddHostedService<Worker>();

            // Cấu hình URL lowercase
            services.Configure<RouteOptions>(options =>
            {
                options.LowercaseUrls = true;
                options.LowercaseQueryStrings = true;
            });

            return services;
        }

        // Phương thức mở rộng để đăng ký các dịch vụ tùy chỉnh, nếu cần
        public static IServiceCollection AddCustomServices(this IServiceCollection services)
        {
            // Cấu hình lifecycle options để đảm bảo shutdown đúng cách
            services.Configure<HostOptions>(options =>
            {
                options.ShutdownTimeout = TimeSpan.FromSeconds(30);
            });

            // Đăng ký các dịch vụ mới, nếu cần
            services.AddSingleton<LogService>();
            services.AddSingleton<UpdateCheckService>();

            // Đảm bảo sử dụng đúng UpdateCheckSettings từ Models namespace
            services.Configure<Models.UpdateCheckSettings>(options =>
            {
                options.Enabled = true;
                options.IntervalMinutes = 60;
                options.AutoUpdateProfiles = true;
            });

            return services;
        }
    }
}