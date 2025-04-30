using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;
using System.Text;
using System.Threading;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Hubs
{
    public class LogHub : Hub
    {
        // Đệm buffer cho các thông báo log để tăng hiệu suất
        private static readonly ConcurrentDictionary<string, StringBuilder> _logBuffers = new ConcurrentDictionary<string, StringBuilder>();
        private static Timer _flushTimer;

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
                        // Không thể gọi SendAsync từ static void method, nên sẽ không flush ở đây
                        // Chỉ xóa buffer nếu có nội dung
                    }
                }
            }
        }

        // Phương thức khi client kết nối
        public async Task SendQueueUpdate(object queueData)
        {
            await Clients.All.SendAsync("ReceiveQueueUpdate", queueData);
        }

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