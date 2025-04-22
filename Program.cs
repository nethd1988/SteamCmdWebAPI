using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Services;
using SteamCmdWebAPI.Filters;
using SteamCmdWebAPI.Middleware;
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
            builder.Services.AddSingleton<UserService>();

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

                // Kiểm tra xem có sử dụng cache hay không
                if (licenseResult.UsingCache)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Đang sử dụng license cache. Ứng dụng vẫn hoạt động trong thời gian grace period.");
                    Console.ResetColor();
                }
                else
                {
                    // Dừng ứng dụng nếu không có cache hợp lệ
                    Environment.Exit(1);
                    return;
                }
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

            // Cấu hình xác thực
            builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.Cookie.HttpOnly = true;
                    options.ExpireTimeSpan = TimeSpan.FromDays(7);
                    options.LoginPath = "/Login";
                    options.LogoutPath = "/Logout";
                    options.AccessDeniedPath = "/Login";
                    options.SlidingExpiration = true;
                });

            // Cấu hình authorization
            builder.Services.AddAuthorization();

            // Đăng ký filters
            builder.Services.AddScoped<RequireFirstUserSetupFilter>();

            // Thêm dịch vụ cơ bản
            builder.Services.AddRazorPages(options => {
                options.Conventions.AddFolderApplicationModelConvention("/", model => {
                    model.Filters.Add(new RequireFirstUserSetupFilter(tempProvider.GetRequiredService<UserService>()));
                });
            });
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

            // Thêm background service kiểm tra license định kỳ
            builder.Services.AddHostedService<LicenseValidationService>();

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

            // Sử dụng middleware kiểm tra tài khoản đầu tiên
            app.UseFirstUserSetup();

            // Xác thực và phân quyền
            app.UseAuthentication();
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

    // Dịch vụ kiểm tra license trong nền
    public class LicenseValidationService : BackgroundService
    {
        private readonly ILogger<LicenseValidationService> _logger;
        private readonly LicenseService _licenseService;
        private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6); // Kiểm tra mỗi 6 giờ

        public LicenseValidationService(ILogger<LicenseValidationService> logger, LicenseService licenseService)
        {
            _logger = logger;
            _licenseService = licenseService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Dịch vụ kiểm tra license đã khởi động");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Chờ trước khi kiểm tra lần đầu
                    await Task.Delay(_checkInterval, stoppingToken);

                    _logger.LogInformation("Đang thực hiện kiểm tra license định kỳ");
                    var licenseResult = await _licenseService.ValidateLicenseAsync();

                    if (licenseResult.IsValid)
                    {
                        _logger.LogInformation("Kiểm tra license định kỳ thành công: {Message}", licenseResult.Message);
                    }
                    else
                    {
                        _logger.LogWarning("Kiểm tra license định kỳ thất bại: {Message}", licenseResult.Message);

                        if (licenseResult.UsingCache)
                        {
                            _logger.LogWarning("Đang sử dụng license cache. Ứng dụng vẫn hoạt động trong thời gian grace period.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi kiểm tra license định kỳ");
                }
            }
        }
    }
}