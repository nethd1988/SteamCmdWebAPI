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
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamCmdWebAPI
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Thiết lập đường dẫn gốc của ứng dụng
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Directory.SetCurrentDirectory(baseDirectory);

            // Tạo builder ứng dụng
            var builder = WebApplication.CreateBuilder(args);

            // Thêm hỗ trợ User Secrets
            if (builder.Environment.IsDevelopment())
            {
                builder.Configuration.AddUserSecrets<Program>();
            }

            // Đăng ký dịch vụ license
            builder.Services.AddSingleton<LicenseService>();

            // Xây dựng provider để kiểm tra license
            var tempProvider = builder.Services.BuildServiceProvider();
            var licenseService = tempProvider.GetRequiredService<LicenseService>();
            var licenseResult = await licenseService.ValidateLicenseAsync();

            if (!licenseResult.IsValid)
            {
                // Log lỗi license
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"LỖI GIẤY PHÉP: {licenseResult.Message}");
                Console.ResetColor();

                // Tạo file báo lỗi
                var errorFilePath = Path.Combine(baseDirectory, "license_error.txt");
                await File.WriteAllTextAsync(errorFilePath, licenseResult.Message);

                // Dừng ứng dụng
                Environment.Exit(1);
                return;
            }

            // Cấu hình để chạy như một Windows Service
            builder.Host.UseWindowsService(options =>
            {
                options.ServiceName = "SteamCmdWebAPI";
            });

            // Đăng ký các dịch vụ
            builder.Services.AddSingleton<EncryptionService>();
            builder.Services.AddSingleton<ProfileService>();
            builder.Services.AddSingleton<SettingsService>();
            builder.Services.AddSingleton<ServerSettingsService>();
            builder.Services.AddSingleton<LogFileReader>();
            builder.Services.AddSingleton<SteamCmdService>();
            builder.Services.AddSingleton<TcpClientService>();

            // Đăng ký dịch vụ Worker
            builder.Services.AddHostedService<Worker>();

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

            // Cấu hình JSON serialization
            builder.Services.AddControllers()
                .AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.WriteIndented = true;
                    options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                    options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });

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
            builder.Logging.AddEventLog(settings =>
            {
                settings.SourceName = "SteamCmdWebAPI";
                settings.LogName = "Application";
            });

            // Cấu hình log levels
            builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

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
                app.UseHsts();
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

            // Tạo thư mục data nếu chưa tồn tại
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
                Console.WriteLine($"Đã tạo thư mục data tại {dataDir}");
            }

            // Log địa chỉ truy cập
            Console.WriteLine("Địa chỉ truy cập:");
            Console.WriteLine($"HTTP: http://0.0.0.0:5288");

            // Chạy ứng dụng
            await app.RunAsync();
        }
    }
}