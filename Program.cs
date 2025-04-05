using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Services;

namespace SteamCmdWebAPI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorPages();
            builder.Services.AddSignalR();
            builder.Services.AddControllers();

            // Đăng ký các service cần thiết
            builder.Services.AddSingleton<ProfileService>();
            builder.Services.AddSingleton<SettingsService>();
            builder.Services.AddSingleton<ServerSettingsService>();
            builder.Services.AddSingleton<SteamCmdService>();
            builder.Services.AddSingleton<EncryptionService>();
            builder.Services.AddSingleton<TcpClientService>();
            builder.Services.AddSingleton<ServerSyncService>(); // Đăng ký ServerSyncService
            builder.Services.AddHostedService<AutoRunBackgroundService>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
                app.UseHsts();
            }

            app.UseHttpsRedirection();
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapRazorPages();
                endpoints.MapHub<LogHub>("/logHub");
                endpoints.MapControllers();
                
                // Map default route
                endpoints.MapGet("/", context =>
                {
                    context.Response.Redirect("/Index");
                    return System.Threading.Tasks.Task.CompletedTask;
                });
            });

            app.Run();
        }
    }
}