using Microsoft.Extensions.DependencyInjection;

namespace SteamCmdWebAPI.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSteamCmdServices(this IServiceCollection services)
        {
            // Đăng ký các service theo thứ tự phụ thuộc
            services.AddSingleton<EncryptionService>();
            services.AddSingleton<ProfileService>();
            services.AddSingleton<SettingsService>();
            services.AddSingleton<ServerSettingsService>();
            services.AddSingleton<SteamCmdService>();
            services.AddSingleton<TcpClientService>();
            services.AddSingleton<ServerSyncService>();
            services.AddSingleton<SilentSyncService>();
            services.AddSingleton<LogFileReader>();

            // Cấu hình AutoRun
            services.AddSingleton<AutoRunConfiguration>();
            services.AddHostedService<AutoRunService>();
            services.AddHostedService<AutoRunBackgroundService>();

            return services;
        }
    }
}