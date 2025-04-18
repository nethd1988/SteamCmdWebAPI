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

        // Phương thức để server yêu cầu mã 2FA từ client
        // Phương thức để server yêu cầu mã 2FA từ client
        // Điều chỉnh phương thức RequestTwoFactorCode
        public static async Task<string> RequestTwoFactorCode(int profileId, IHubContext<LogHub> hubContext)
        {
            var tcs = new TaskCompletionSource<string>();
            _twoFactorCodeTasks.TryRemove(profileId, out _); // Xóa task cũ nếu có
            _twoFactorCodeTasks.TryAdd(profileId, tcs);

            // Gửi yêu cầu trực tiếp
            await hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);

            // Gửi cả thông báo rõ ràng cho giao diện người dùng
            string requestMessage = $"STEAMGUARD_REQUEST_{profileId}: Steam Guard code được yêu cầu cho profile ID {profileId}. Vui lòng nhập mã xác thực đã gửi đến email hoặc ứng dụng di động của bạn.";
            await hubContext.Clients.All.SendAsync("ReceiveLog", requestMessage);

            // Giảm thời gian chờ
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60)); // 60 giây timeout
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _twoFactorCodeTasks.TryRemove(profileId, out _);
                await hubContext.Clients.All.SendAsync("ReceiveLog", $"Hết thời gian đợi mã 2FA cho profile ID {profileId}");
                return string.Empty;
            }

            return await tcs.Task;
        }
    }
}