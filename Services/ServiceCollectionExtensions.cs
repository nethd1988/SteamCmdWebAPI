using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting; // Added for IHostedService

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
            services.AddSingleton<SteamApiService>(); // Đăng ký SteamApiService trước
            services.AddSingleton<SteamCmdService>(); // SteamCmdService cần SteamApiService, nên đăng ký sau
            services.AddSingleton<TcpClientService>();


            // Cấu hình AutoRun và UpdateCheck
            services.AddSingleton<AutoRunConfiguration>();
            services.AddHostedService<AutoRunService>(); // Nếu vẫn cần dịch vụ này
            services.AddHostedService<AutoRunBackgroundService>(); // Nếu vẫn cần dịch vụ này
            services.AddHostedService<UpdateCheckService>(); // UpdateCheckService cần SteamApiService và SteamCmdService

            return services;
        }
    }
}
