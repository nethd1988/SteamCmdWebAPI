using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System.Collections.Concurrent;

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
        public static Task<string> RequestTwoFactorCode(int profileId, IHubContext<LogHub> hubContext)
        {
            var tcs = new TaskCompletionSource<string>();
            _twoFactorCodeTasks.TryAdd(profileId, tcs);
            hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);
            return tcs.Task;
        }
    }
}