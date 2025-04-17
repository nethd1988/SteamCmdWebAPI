using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;

namespace SteamCmdWebAPI.Hubs
{
    public class LogHub : Hub
    {
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _twoFactorCodeTasks = new ConcurrentDictionary<int, TaskCompletionSource<string>>();

        public Task SubmitTwoFactorCode(int profileId, string twoFactorCode)
        {
            if (_twoFactorCodeTasks.TryRemove(profileId, out var tcs))
            {
                tcs.SetResult(twoFactorCode);
            }
            return Task.CompletedTask;
        }

        public static async Task<string> RequestTwoFactorCode(int profileId, IHubContext<LogHub> hubContext)
        {
            var tcs = new TaskCompletionSource<string>();
            _twoFactorCodeTasks.TryRemove(profileId, out _);
            _twoFactorCodeTasks.TryAdd(profileId, tcs);

            await hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);
            await hubContext.Clients.All.SendAsync("ReceiveLog", $"STEAMGUARD_REQUEST_{profileId}");

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(120));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _twoFactorCodeTasks.TryRemove(profileId, out _);
                return string.Empty;
            }

            return await tcs.Task;
        }

        public Task CancelTwoFactorRequest(int profileId)
        {
            if (_twoFactorCodeTasks.TryRemove(profileId, out var tcs))
            {
                tcs.SetResult(string.Empty);
            }
            return Clients.All.SendAsync("CancelTwoFactorRequest", profileId);
        }
    }
}