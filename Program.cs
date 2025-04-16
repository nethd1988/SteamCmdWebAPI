using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
            try
            {
                // Đảm bảo thư mục data và steamcmd tồn tại
                string baseDir = AppContext.BaseDirectory;
                string dataDir = Path.Combine(baseDir, "data");
                string steamCmdDir = Path.Combine(baseDir, "steamcmd");

                if (!Directory.Exists(dataDir))
                    Directory.CreateDirectory(dataDir);

                if (!Directory.Exists(steamCmdDir))
                    Directory.CreateDirectory(steamCmdDir);

                // Ghi log khởi động
                File.AppendAllText(
                    Path.Combine(baseDir, "startup_log.txt"),
                    $"{DateTime.Now}: Ứng dụng đang khởi động...\n");

                var builder = WebApplication.CreateBuilder(args);

                File.AppendAllText(
                    Path.Combine(baseDir, "startup_log.txt"),
                    $"{DateTime.Now}: Đã tạo builder\n");

                // Thêm các dịch vụ cơ bản
                builder.Services.AddRazorPages().AddRazorPagesOptions(options =>
                {
                    options.Conventions.AddPageRoute("/Index", "");
                });
                builder.Services.AddSignalR();
                builder.Services.AddControllers();

                File.AppendAllText(
                    Path.Combine(baseDir, "startup_log.txt"),
                    $"{DateTime.Now}: Đã thêm RazorPages\n");

                // Đăng ký các service theo thứ tự phụ thuộc
                builder.Services.AddSingleton<EncryptionService>();
                builder.Services.AddSingleton<ProfileService>();
                builder.Services.AddSingleton<SettingsService>();
                builder.Services.AddSingleton<ServerSettingsService>();
                builder.Services.AddSingleton<TcpClientService>();
                builder.Services.AddSingleton<SteamCmdService>();
                builder.Services.AddSingleton<ServerSyncService>();
                builder.Services.AddHostedService<AutoRunBackgroundService>();

                // Cấu hình CORS
                builder.Services.AddCors(options =>
                {
                    options.AddPolicy("AllowAll", builder =>
                    {
                        builder.AllowAnyOrigin()
                               .AllowAnyMethod()
                               .AllowAnyHeader();
                    });
                });

                var app = builder.Build();

                File.AppendAllText(
                    Path.Combine(baseDir, "startup_log.txt"),
                    $"{DateTime.Now}: Đã build ứng dụng\n");

                if (app.Environment.IsDevelopment())
                {
                    app.UseDeveloperExceptionPage();
                }
                else
                {
                    app.UseExceptionHandler("/Error");
                    app.UseHsts();
                }

                app.UseCors("AllowAll");
                app.UseHttpsRedirection();
                app.UseStaticFiles();
                app.UseRouting();
                app.UseAuthorization();

                app.MapRazorPages();
                app.MapHub<LogHub>("/logHub");
                app.MapControllers();

                app.MapGet("/", context =>
                {
                    context.Response.Redirect("/Index");
                    return System.Threading.Tasks.Task.CompletedTask;
                });

                File.AppendAllText(
                    Path.Combine(baseDir, "startup_log.txt"),
                    $"{DateTime.Now}: Chuẩn bị chạy ứng dụng\n");

                app.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khởi động ứng dụng: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup_error.log"),
                    $"{DateTime.Now}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}