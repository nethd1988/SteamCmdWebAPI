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
                string baseDir = Directory.GetCurrentDirectory();
                string dataDir = Path.Combine(baseDir, "data");
                string steamCmdDir = Path.Combine(baseDir, "steamcmd");

                if (!Directory.Exists(dataDir))
                    Directory.CreateDirectory(dataDir);

                if (!Directory.Exists(steamCmdDir))
                    Directory.CreateDirectory(steamCmdDir);

                var builder = WebApplication.CreateBuilder(args);

                // Thêm các dịch vụ cơ bản
                builder.Services.AddRazorPages();
                builder.Services.AddSignalR();
                builder.Services.AddControllers();

                // Đăng ký các service theo thứ tự phụ thuộc
                builder.Services.AddSingleton<EncryptionService>();
                builder.Services.AddSingleton<ProfileService>();
                builder.Services.AddSingleton<SettingsService>();
                builder.Services.AddSingleton<ServerSettingsService>();
                builder.Services.AddSingleton<TcpClientService>();
                builder.Services.AddSingleton<SteamCmdService>();
                builder.Services.AddSingleton<ServerSyncService>();
                builder.Services.AddHostedService<AutoRunBackgroundService>();

                var app = builder.Build();

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

                app.MapGet("/", context =>
                {
                    context.Response.Redirect("/Index");
                    return System.Threading.Tasks.Task.CompletedTask;
                });

                app.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi khởi động ứng dụng: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "startup_error.log"),
                    $"{DateTime.Now}: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}