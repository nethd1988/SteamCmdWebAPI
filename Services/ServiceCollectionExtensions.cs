using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Routing;

namespace SteamCmdWebAPI.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSteamCmdServices(this IServiceCollection services)
        {
            // Đăng ký các service theo thứ tự phụ thuộc
            services.AddSingleton<LicenseService>();
            services.AddSingleton<EncryptionService>();
            services.AddSingleton<ProfileService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<ServerSettingsService>();
            services.AddSingleton<LogFileReader>();
            services.AddSingleton<SteamApiService>();
            services.AddSingleton<LogService>();
            services.AddSingleton<DependencyManagerService>();

            // Đảm bảo QueueService được đăng ký TRƯỚC SteamCmdService
            services.AddSingleton<QueueService>();
            services.AddSingleton<SteamCmdService>();
            services.AddSingleton<TcpClientService>();
            services.AddSingleton<UserService>();

            // Cấu hình AutoRun và UpdateCheck
            services.AddSingleton<AutoRunConfiguration>();
            services.AddHostedService<AutoRunBackgroundService>();
            services.AddHostedService<UpdateCheckService>();

            // Thêm HeartbeatService
            services.AddHostedService<HeartbeatService>();

            // Cấu hình URL lowercase
            services.Configure<RouteOptions>(options =>
            {
                options.LowercaseUrls = true;
                options.LowercaseQueryStrings = true;
            });

            return services;
        }
    }
}