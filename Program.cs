using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Services;
using System;
using System.IO;

namespace SteamCmdWebAPI
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Thiết lập đường dẫn gốc của ứng dụng
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(baseDirectory);

            // Đảm bảo thư mục data tồn tại
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                Console.WriteLine($"Đã tạo thư mục data tại {dataDir}");
            }

            // Add services to the container.
            builder.Services.AddRazorPages();
            builder.Services.AddSignalR();
            builder.Services.AddControllers();

            // Cấu hình Logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();
            builder.Logging.AddEventSourceLogger();

            // Đăng ký các service cần thiết
            builder.Services.AddSingleton<ProfileService>();
            builder.Services.AddSingleton<SettingsService>();
            builder.Services.AddSingleton<ServerSettingsService>();
            builder.Services.AddSingleton<SteamCmdService>();
            builder.Services.AddSingleton<EncryptionService>();
            builder.Services.AddSingleton<TcpClientService>();
            builder.Services.AddSingleton<ServerSyncService>();
            builder.Services.AddSingleton<SilentSyncService>();
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

            app.MapRazorPages();
            app.MapHub<LogHub>("/logHub");
            app.MapControllers();

            // Map default route
            app.MapGet("/", context => {
                context.Response.Redirect("/Index");
                return Task.CompletedTask;
            });

            // Địa chỉ truy cập
            Console.WriteLine($"Ứng dụng khởi động tại: https://localhost:7288 và http://localhost:5288");
            Console.WriteLine($"Thư mục gốc: {baseDirectory}");

            app.Run();
        }
    }
}