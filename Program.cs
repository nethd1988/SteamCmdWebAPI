using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSignalR();

// Đăng ký các service cần thiết
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<ServerSettingsService>();
builder.Services.AddSingleton<SteamCmdService>();
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSingleton<TcpClientService>();
builder.Services.AddSingleton<SilentSyncService>(); // Thêm dịch vụ SilentSync
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

app.UseEndpoints(endpoints =>
{
    endpoints.MapRazorPages();
    endpoints.MapHub<LogHub>("/logHub");
    // Thêm route mặc định để trỏ đến /Index
    endpoints.MapGet("/", async context =>
    {
        context.Response.Redirect("/Index");
        await Task.CompletedTask;
    });
});

app.Run();