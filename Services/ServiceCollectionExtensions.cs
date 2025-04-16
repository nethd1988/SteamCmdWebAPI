using Microsoft.Extensions.DependencyInjection;

namespace SteamCmdWebAPI.Services
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddSteamCmdServices(this IServiceCollection services)
        {
            // Đăng ký các service với thứ tự phụ thuộc đúng
            services.AddSingleton<ProfileService>();
            services.AddSingleton<SteamCmdService>();
            services.AddSingleton<AutoRunConfiguration>();
            services.AddHostedService<AutoRunService>();
            
            return services;
        }
    }
}
