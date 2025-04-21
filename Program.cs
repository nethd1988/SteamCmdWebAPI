using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Services;
using System;
using System.IO;
using System.Threading.Tasks;

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

            // Tối ưu hiệu suất SignalR
            builder.Services.AddSignalR(options =>
            {
                // Tăng kích thước buffer tối đa để giảm trễ
                options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB
                options.StreamBufferCapacity = 20;

                // Giảm công việc ở backend threads
                options.EnableDetailedErrors = false;
                options.HandshakeTimeout = TimeSpan.FromSeconds(10);
                options.KeepAliveInterval = TimeSpan.FromSeconds(15);

                // Quản lý streaming
                options.MaximumParallelInvocationsPerClient = 2;
                options.MaximumParallelInvocations = 100;
            })
            .AddJsonProtocol(options =>
            {
                // Giảm kích thước JSON được truyền đi
                options.PayloadSerializerOptions.WriteIndented = false;
            });

            builder.Services.AddControllers();

            // Cấu hình Logging
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole();
            builder.Logging.AddDebug();
            builder.Logging.AddEventSourceLogger();

            // Cấu hình bộ lọc chi tiết log
            builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Warning);
            builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Warning);

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

            // Tối ưu hiệu suất cho file tĩnh
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    // Cache file CSS và JS trong 7 ngày
                    if (ctx.File.Name.EndsWith(".css") || ctx.File.Name.EndsWith(".js"))
                    {
                        ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=604800");
                    }
                }
            });

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

            // Tối ưu hiệu suất chung cho ứng dụng
            app.Use(async (context, next) => {
                // Thêm header hiệu suất
                context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
                context.Response.Headers.Add("X-Frame-Options", "SAMEORIGIN");

                // Đo thời gian xử lý request
                var sw = System.Diagnostics.Stopwatch.StartNew();

                try
                {
                    await next();
                }
                finally
                {
                    sw.Stop();

                    // Log các request chậm (trên 500ms)
                    if (sw.ElapsedMilliseconds > 500)
                    {
                        app.Logger.LogWarning("Request chậm {Path}: {ElapsedMs}ms",
                            context.Request.Path, sw.ElapsedMilliseconds);
                    }
                }
            });

            // Địa chỉ truy cập
            Console.WriteLine($"Ứng dụng khởi động tại: https://localhost:7288 và http://localhost:5288");
            Console.WriteLine($"Thư mục gốc: {baseDirectory}");

            app.Run();
        }
    }
}