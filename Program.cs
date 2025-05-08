using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Authorization;
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
using Microsoft.AspNetCore.Authentication;
using SteamCmdWebAPI.Models;
using System.Threading;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using System.Runtime.InteropServices;

namespace SteamCmdWebAPI
{
    // Thêm class để tương tác với WinAPI để ẩn console
    internal static class Native
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        internal const int SW_HIDE = 0;
        internal const int SW_SHOW = 5;
    }

    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Kiểm tra tham số --quiet để ẩn console
            bool quietMode = args.Contains("--quiet");
            
            if (quietMode)
            {
                // Ẩn console window khi chạy ở chế độ quiet
                var handle = Native.GetConsoleWindow();
                Native.ShowWindow(handle, Native.SW_HIDE);
            }
            
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
            builder.Services.AddSingleton<Services.LicenseService>();
            builder.Services.AddSingleton<Services.UserService>();

            // Xây dựng provider để kiểm tra license
            var tempProvider = builder.Services.BuildServiceProvider();
            var licenseService = tempProvider.GetRequiredService<Services.LicenseService>();
            var licenseResult = await licenseService.ValidateLicenseAsync();

            // Thêm biến toàn cục để kiểm soát trạng thái license
            var isLicenseValid = licenseResult.IsValid || licenseResult.UsingCache;
            var licenseStateService = new LicenseStateService
            {
                IsLicenseValid = isLicenseValid,
                LicenseMessage = licenseResult.Message,
                UsingCache = licenseResult.UsingCache
            };
            builder.Services.AddSingleton(licenseStateService);
            licenseStateService.LogLicenseState();

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
                    // Ghi log và khóa ứng dụng
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("CẢNH BÁO: TẤT CẢ CHỨC NĂNG CỦA ỨNG DỤNG ĐÃ BỊ KHÓA!");
                    Console.WriteLine("Ứng dụng đang chạy với tất cả chức năng bị khóa do không có giấy phép hợp lệ.");
                    Console.WriteLine("Vui lòng kiểm tra lại giấy phép hoặc liên hệ nhà cung cấp.");
                    Console.ResetColor();
                    
                    // Tạo file báo lỗi chi tiết
                    var detailedErrorPath = Path.Combine(baseDirectory, "license_detailed_error.txt");
                    var errorDetails = $"Thời gian: {DateTime.Now}\n" +
                                      $"Lỗi: {licenseResult.Message}\n" +
                                      $"Trạng thái: Không có giấy phép hợp lệ\n" +
                                      $"Cache: Không có\n" +
                                      $"API: Không kết nối được\n" +
                                      $"Ghi chú: Service vẫn chạy nhưng tất cả chức năng đã bị khóa";
                    await File.WriteAllTextAsync(detailedErrorPath, errorDetails);
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
            builder.Services.AddSingleton<SteamApiService>();
            builder.Services.AddSingleton<DependencyManagerService>();
            builder.Services.AddSingleton<SteamCmdService>();
            builder.Services.AddSingleton<TcpClientService>();
            builder.Services.AddSteamCmdServices();

            // Đăng ký các dịch vụ mới
            builder.Services.AddSingleton<UpdateCheckService>();
            builder.Services.AddHostedService(provider => provider.GetRequiredService<UpdateCheckService>());

            // Đăng ký dịch vụ mã hóa tài khoản khi khởi động
            builder.Services.AddHostedService<AccountEncryptionService>();
            
            // Đăng ký dịch vụ quét tự động tài khoản Steam
            builder.Services.AddHostedService<AutoScanBackgroundService>();

            // Đăng ký model mới
            builder.Services.Configure<Models.UpdateCheckSettings>(builder.Configuration.GetSection("UpdateCheckSettings"));

            // Đăng ký dịch vụ Worker - luôn chạy kể cả khi license không hợp lệ
            builder.Services.AddHostedService<Worker>();

            // Đăng ký HeartbeatService với cấu hình đặc biệt
            builder.Services.AddHostedService<HeartbeatService>(sp =>
            {
                var logger = sp.GetRequiredService<ILogger<HeartbeatService>>();
                var tcpClientService = sp.GetRequiredService<TcpClientService>();
                return new HeartbeatService(logger, tcpClientService);
            });

            // Cấu hình lifecycle options để đảm bảo shutdown đúng cách
            builder.Services.Configure<HostOptions>(options =>
            {
                options.ShutdownTimeout = TimeSpan.FromSeconds(30);
            });

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
                    options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None; // Cho phép HTTP
                    options.ExpireTimeSpan = TimeSpan.FromDays(7);
                    options.LoginPath = "/Login";
                    options.LogoutPath = "/Logout";
                    options.AccessDeniedPath = "/Login";
                    options.SlidingExpiration = true;
                    options.ReturnUrlParameter = "ReturnUrl";

                    // Xử lý cookie validation lỗi
                    options.Events = new CookieAuthenticationEvents
                    {
                        OnValidatePrincipal = async context =>
                        {
                            var userService = context.HttpContext.RequestServices.GetRequiredService<Services.UserService>();
                            var userIdClaim = context.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                            if (userIdClaim != null && int.TryParse(userIdClaim, out int userId))
                            {
                                var user = userService.GetUserById(userId);
                                if (user == null)
                                {
                                    context.RejectPrincipal();
                                    await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                                }
                            }
                        }
                    };
                });

            // Cấu hình authorization
            builder.Services.AddAuthorization(options =>
            {
                // Cấu hình policy mặc định yêu cầu xác thực
                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();

                // Thêm policy khác nếu cần
                options.AddPolicy("AdminOnly", policy =>
                    policy.RequireRole("Admin"));
            });

            // Đăng ký filters
            builder.Services.AddScoped<RequireFirstUserSetupFilter>();

            // Thêm dịch vụ cơ bản - bắt buộc authorize cho tất cả Razor Pages
            builder.Services.AddRazorPages(options => {
                // Áp dụng filter để kiểm tra người dùng đầu tiên
                options.Conventions.AddFolderApplicationModelConvention("/", model => {
                    model.Filters.Add(new RequireFirstUserSetupFilter(
                        tempProvider.GetRequiredService<Services.UserService>(),
                        tempProvider.GetRequiredService<ILogger<RequireFirstUserSetupFilter>>()
                    ));
                });

                // Đặt trang Dashboard làm trang mặc định
                options.Conventions.AddPageRoute("/Dashboard", "/");
                options.Conventions.AddPageRoute("/Dashboard", "/Index");

                // Áp dụng filter Authorize cho tất cả các trang trừ Login/Register/Error
                options.Conventions.AuthorizeFolder("/");
                options.Conventions.AllowAnonymousToPage("/Login");
                options.Conventions.AllowAnonymousToPage("/Register");
                options.Conventions.AllowAnonymousToPage("/Error");
                options.Conventions.AllowAnonymousToPage("/LicenseError");
            });

            // Áp dụng authorize cho controllers
            builder.Services.AddControllers(options => {
                options.Filters.Add(new AuthorizeFilter());
            });

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

            // Đăng ký LogService
            builder.Services.AddSingleton<LogService>();

            // Cấu hình log levels
            builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

            // Đăng ký dịch vụ kiểm tra license định kỳ
            builder.Services.AddHostedService<LicenseValidationService>();

            // Đảm bảo sử dụng đúng UpdateCheckSettings từ Models namespace
            builder.Services.Configure<Models.UpdateCheckSettings>(builder.Configuration.GetSection("UpdateCheckSettings"));

            // Thêm endpoint cho kiểm tra session
            builder.Services.AddControllers().AddApplicationPart(typeof(Program).Assembly);

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

            // Định tuyến trước khi xác thực
            app.UseRouting();

            // Xác thực TRƯỚC phân quyền và bất kỳ middleware tùy chỉnh nào
            app.UseAuthentication();
            app.UseAuthorization();

            // API Error Handling Middleware
            app.Use(async (context, next) =>
            {
                // Xử lý chuyển hướng API sang Dashboard
                if (context.Request.Path.StartsWithSegments("/api") &&
                    !context.Request.Path.StartsWithSegments("/api/auth") &&
                    context.User?.Identity?.IsAuthenticated == true)
                {
                    context.Response.Redirect("/Dashboard");
                    return;
                }
                await next();
            });

            // Dashboard Redirect Middleware - chuyển hướng các URL cơ bản đến Dashboard
            app.Use(async (context, next) =>
            {
                if ((context.Request.Path == "/" ||
                     context.Request.Path == "/Index" ||
                     context.Request.Path == "/api" ||
                     context.Request.Path == "/api/Index") &&
                    context.User?.Identity?.IsAuthenticated == true)
                {
                    context.Response.Redirect("/Dashboard");
                    return;
                }
                await next();
            });

            // Middleware kiểm tra người dùng đầu tiên (đặt sau xác thực)
            app.UseFirstUserSetup();

            // Thêm middleware kiểm tra license
            app.UseLicenseCheck();

            // Khởi tạo dịch vụ license validation để kiểm tra định kỳ
            app.UseMiddleware<Middleware.LicenseCheckMiddleware>();

            // Endpoint routing
            app.MapRazorPages();
            app.MapControllers();
            app.MapHub<LogHub>("/logHub");
            app.MapHub<LogHub>("/steamHub");

            // Đặt Dashboard làm trang chủ mặc định
            app.MapGet("/", async context =>
            {
                if (context.User?.Identity?.IsAuthenticated == true)
                {
                    context.Response.Redirect("/Dashboard");
                }
                else
                {
                    context.Response.Redirect("/Login");
                }
            });

            // API route cho kiểm tra session
            app.MapControllerRoute(
                name: "api",
                pattern: "api/{controller=Home}/{action=Index}/{id?}");

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

            // Log thông tin license
            if (!licenseResult.IsValid)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("LƯU Ý: Service đang chạy với chức năng bị hạn chế do không có giấy phép hợp lệ.");
                Console.ResetColor();
            }

            // Chạy ứng dụng
            await app.RunAsync();
        }
    }

    // Dịch vụ lưu trữ trạng thái license
    public class LicenseStateService
    {
        public bool IsLicenseValid { get; set; }
        public string LicenseMessage { get; set; }
        public bool UsingCache { get; set; }
        
        // Thêm thuộc tính để kiểm tra có cần khóa tất cả chức năng hay không
        public bool LockAllFunctions => !IsLicenseValid && !UsingCache;

        public void LogLicenseState()
        {
            // Implementation for logging license state
        }
    }

    // Dịch vụ kiểm tra license trong nền
    public class LicenseValidationService : BackgroundService
    {
        private readonly ILogger<LicenseValidationService> _logger;
        private readonly Services.LicenseService _licenseService;
        private readonly LicenseStateService _licenseStateService;
        private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6); // Kiểm tra mỗi 6 giờ

        public LicenseValidationService(ILogger<LicenseValidationService> logger, Services.LicenseService licenseService, LicenseStateService licenseStateService)
        {
            _logger = logger;
            _licenseService = licenseService;
            _licenseStateService = licenseStateService;
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

                    // Cập nhật trạng thái license
                    _licenseStateService.IsLicenseValid = licenseResult.IsValid || licenseResult.UsingCache;
                    _licenseStateService.LicenseMessage = licenseResult.Message;
                    _licenseStateService.UsingCache = licenseResult.UsingCache;

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
                        else
                        {
                            _logger.LogInformation("Service vẫn chạy nhưng với chức năng bị hạn chế.");
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