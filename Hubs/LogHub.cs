using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;

namespace SteamCmdWebAPI.Hubs
{
    public class LogHub : Hub
    {
        // Dictionary để lưu trữ TaskCompletionSource cho từng profileId
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _twoFactorCodeTasks = new ConcurrentDictionary<int, TaskCompletionSource<string>>();

        // Phương thức để client gửi mã 2FA về server
        public Task SubmitTwoFactorCode(int profileId, string twoFactorCode)
        {
            if (_twoFactorCodeTasks.TryRemove(profileId, out var tcs))
            {
                tcs.SetResult(twoFactorCode);
            }
            return Task.CompletedTask;
        }

        // Phương thức để client gửi lệnh hoặc dữ liệu nhập khác
        public Task SubmitInput(int profileId, string input)
        {
            // TODO: Xử lý dữ liệu nhập từ client
            return Clients.All.SendAsync("ReceiveLog", $"Đã nhận dữ liệu nhập: {input}");
        }

        // Phương thức để server yêu cầu mã 2FA từ client (cải tiến)
        public static async Task<string> RequestTwoFactorCode(int profileId, IHubContext<LogHub> hubContext)
        {
            var tcs = new TaskCompletionSource<string>();
            _twoFactorCodeTasks.TryRemove(profileId, out _); // Xóa task cũ nếu có
            _twoFactorCodeTasks.TryAdd(profileId, tcs);

            // Gửi yêu cầu riêng biệt để kích hoạt giao diện nhập 2FA
            await hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);

            // Gửi thông báo rõ ràng về yêu cầu 2FA
            string requestMessage = $"[STEAM GUARD] Yêu cầu nhập mã xác thực cho profile ID {profileId}";
            await hubContext.Clients.All.SendAsync("ReceiveLog", requestMessage);

            // Giảm thời gian chờ xuống 5 phút
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _twoFactorCodeTasks.TryRemove(profileId, out _);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"Hết thời gian đợi mã 2FA cho profile ID {profileId}");
                return string.Empty;
            }

            return await tcs.Task;
        }

        // Phương thức khi client kết nối
        public override async Task OnConnectedAsync()
        {
            await Clients.Caller.SendAsync("ReceiveLog", "Đã kết nối với máy chủ SteamAUTO thành công!");
            await base.OnConnectedAsync();
        }

        // Phương thức khi client ngắt kết nối
        public override async Task OnDisconnectedAsync(Exception exception)
        {
            if (exception != null)
            {
                await Clients.Others.SendAsync("ReceiveLog", $"Một client đã ngắt kết nối do lỗi: {exception.Message}");
            }
            await base.OnDisconnectedAsync(exception);
        }
    }
}