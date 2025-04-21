using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;
using System.Text;
using System.Threading;

namespace SteamCmdWebAPI.Hubs
{
    public class LogHub : Hub
    {
        // Dictionary để lưu trữ TaskCompletionSource cho từng profileId
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _twoFactorCodeTasks = new ConcurrentDictionary<int, TaskCompletionSource<string>>();
        private static readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _consoleInputTasks = new ConcurrentDictionary<int, TaskCompletionSource<string>>();

        // Đệm buffer cho các thông báo log để tăng hiệu suất
        private static readonly ConcurrentDictionary<string, StringBuilder> _logBuffers = new ConcurrentDictionary<string, StringBuilder>();
        private static Timer _flushTimer;

        // Thêm biến để theo dõi yêu cầu 2FA đang xử lý
        private static readonly ConcurrentDictionary<int, bool> _processing2FARequests = new ConcurrentDictionary<int, bool>();

        static LogHub()
        {
            // Tạo timer để đẩy buffer log định kỳ
            _flushTimer = new Timer(FlushLogBuffers, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }

        private static void FlushLogBuffers(object state)
        {
            foreach (var key in _logBuffers.Keys)
            {
                if (_logBuffers.TryRemove(key, out StringBuilder buffer))
                {
                    string content = buffer.ToString();
                    if (!string.IsNullOrEmpty(content))
                    {
                        // Không thể gọi SendAsync từ static void method, cần cơ chế khác nếu muốn gửi log
                    }
                }
            }
        }

        // Phương thức để client gửi mã 2FA về server
        public Task SubmitTwoFactorCode(int profileId, string twoFactorCode)
        {
            if (_twoFactorCodeTasks.TryRemove(profileId, out var tcs))
            {
                tcs.TrySetResult(twoFactorCode);
            }

            // Cũng kiểm tra nếu đang chờ đầu vào console
            if (_consoleInputTasks.TryRemove(profileId, out var inputTcs))
            {
                inputTcs.TrySetResult(twoFactorCode);
            }

            return Task.CompletedTask;
        }

        // Phương thức mới cho việc nhập từ console
        public Task SubmitConsoleInput(int profileId, string input)
        {
            if (_consoleInputTasks.TryRemove(profileId, out var tcs))
            {
                tcs.TrySetResult(input);
            }
            return Task.CompletedTask;
        }

        // Phương thức để kiểm tra xem có đang xử lý 2FA cho profile không
        public static bool IsProcessing2FA(int profileId)
        {
            return _processing2FARequests.ContainsKey(profileId) && _processing2FARequests[profileId];
        }

        // Phương thức để yêu cầu mã 2FA trực tiếp từ console
        public static async Task<string> RequestTwoFactorCodeFromConsole(int profileId, IHubContext<LogHub> hubContext)
        {
            // Đánh dấu đang xử lý 2FA cho profile này
            _processing2FARequests.TryAdd(profileId, true);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _consoleInputTasks.TryRemove(profileId, out _); // Xóa task cũ nếu có
            _consoleInputTasks.TryAdd(profileId, tcs);

            // Gửi yêu cầu bật chế độ nhập console và thông báo rõ ràng
            await hubContext.Clients.All.SendAsync("EnableConsoleInput", profileId);
            await hubContext.Clients.All.SendAsync("ReceiveLog", $"=== STEAM GUARD: Vui lòng nhập mã xác thực cho profile ID {profileId} ===");

            // Timeout sau 5 phút với CancellationToken để giải phóng tài nguyên
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);

            try
            {
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _consoleInputTasks.TryRemove(profileId, out _);
                    await hubContext.Clients.All.SendAsync("ReceiveLog", $"Hết thời gian đợi mã 2FA cho profile ID {profileId}");
                    await hubContext.Clients.All.SendAsync("DisableConsoleInput");
                    _processing2FARequests.TryRemove(profileId, out _);
                    return string.Empty;
                }

                // Vô hiệu hóa input sau khi đã nhận mã
                await hubContext.Clients.All.SendAsync("DisableConsoleInput");
                _processing2FARequests.TryRemove(profileId, out _);
                return await tcs.Task;
            }
            catch
            {
                _consoleInputTasks.TryRemove(profileId, out _);
                await hubContext.Clients.All.SendAsync("DisableConsoleInput");
                _processing2FARequests.TryRemove(profileId, out _);
                return string.Empty;
            }
            finally
            {
                try { cts.Cancel(); } catch { }
                _processing2FARequests.TryRemove(profileId, out _);
            }
        }

        // Phương thức để server yêu cầu mã 2FA từ client (giữ lại để tương thích)
        public static async Task<string> RequestTwoFactorCode(int profileId, IHubContext<LogHub> hubContext)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _twoFactorCodeTasks.TryRemove(profileId, out _); // Xóa task cũ nếu có
            _twoFactorCodeTasks.TryAdd(profileId, tcs);

            // Gửi yêu cầu riêng biệt để kích hoạt giao diện nhập 2FA
            await hubContext.Clients.All.SendAsync("RequestTwoFactorCode", profileId);

            // Gửi thông báo rõ ràng về yêu cầu 2FA
            string requestMessage = $"[STEAM GUARD] Yêu cầu nhập mã xác thực cho profile ID {profileId}";
            await hubContext.Clients.All.SendAsync("ReceiveLog", requestMessage);

            // Timeout sau 5 phút với resource cleanup
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);

            try
            {
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    _twoFactorCodeTasks.TryRemove(profileId, out _);
                    await hubContext.Clients.All.SendAsync("ReceiveLog", $"Hết thời gian đợi mã 2FA cho profile ID {profileId}");
                    return string.Empty;
                }

                return await tcs.Task;
            }
            catch
            {
                _twoFactorCodeTasks.TryRemove(profileId, out _);
                return string.Empty;
            }
            finally
            {
                try { cts.Cancel(); } catch { }
            }
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