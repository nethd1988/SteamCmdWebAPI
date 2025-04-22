using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Services;
using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace SteamCmdWebAPI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Thêm hỗ trợ User Secrets
            if (builder.Environment.IsDevelopment())
            {
                builder.Configuration.AddUserSecrets<Program>();
            }

            // Cấu hình Kestrel để lắng nghe từ tất cả các địa chỉ IP
            builder.WebHost.ConfigureKestrel(serverOptions =>
            {
                // Lắng nghe trên tất cả các địa chỉ IP và port 5288
                serverOptions.Listen(IPAddress.Any, 5288);
            });

            // Cấu hình CORS
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.SetIsOriginAllowed(_ => true)
                          .AllowAnyMethod()
                          .AllowAnyHeader()
                          .AllowCredentials();
                });
            });

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

            // Thêm dịch vụ cơ bản
            builder.Services.AddRazorPages();
            builder.Services.AddControllers();

            // Cấu hình SignalR
            builder.Services.AddSignalR(options =>
            {
                options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10MB
                options.EnableDetailedErrors = true;
                options.HandshakeTimeout = TimeSpan.FromSeconds(30);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            });

            // Cấu hình logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();

            // Đăng ký dịch vụ 
            builder.Services.AddSteamCmdServices();

            // Xây dựng ứng dụng
            var app = builder.Build();

            // Cấu hình middleware
            if (app.Environment.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                app.UseExceptionHandler("/Error");
            }

            // Sử dụng CORS
            app.UseCors();

            // Middleware khác
            app.UseStaticFiles();
            app.UseRouting();
            app.UseAuthorization();

            // Endpoint routing
            app.MapRazorPages();
            app.MapControllers();
            app.MapHub<LogHub>("/logHub");

            // Log địa chỉ truy cập
            Console.WriteLine("Địa chỉ truy cập:");
            Console.WriteLine($"HTTP: http://0.0.0.0:5288");

            // Chạy ứng dụng
            await app.RunAsync();
        }
    }
}