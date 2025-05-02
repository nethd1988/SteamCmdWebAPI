using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System;
using System.Text;
using System.Threading;
using SteamCmdWebAPI.Models;
using SteamCmdWebAPI.Services;
using Microsoft.Extensions.Logging;

namespace SteamCmdWebAPI.Hubs
{
    public class LogHub : Hub
    {
        // Đệm buffer cho các thông báo log để tăng hiệu suất
        private static readonly ConcurrentDictionary<string, StringBuilder> _logBuffers = new ConcurrentDictionary<string, StringBuilder>();
        private static Timer _flushTimer;
        private readonly ILogger<LogHub> _logger;

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
            
            // Gửi thông tin hàng đợi khi client kết nối
            try
            {
                var queueService = Context.GetHttpContext().RequestServices.GetService<QueueService>();
                if (queueService != null)
                {
                    var currentQueue = queueService.GetQueue();
                    var queueHistory = queueService.GetQueueHistory();
                    await Clients.Caller.SendAsync("ReceiveQueueUpdate", 
                        new { CurrentQueue = currentQueue, QueueHistory = queueHistory });
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveLog", $"Lỗi khi lấy thông tin hàng đợi: {ex.Message}");
            }
            
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

        // Thêm phương thức mới để yêu cầu cập nhật hàng đợi từ client
        public async Task RequestQueueUpdate()
        {
            try
            {
                var queueService = Context.GetHttpContext().RequestServices.GetService<QueueService>();
                if (queueService != null)
                {
                    var currentQueue = queueService.GetQueue();
                    var queueHistory = queueService.GetQueueHistory();
                    await Clients.Caller.SendAsync("ReceiveQueueUpdate", 
                        new { CurrentQueue = currentQueue, QueueHistory = queueHistory });
                }
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveLog", $"Lỗi khi lấy thông tin hàng đợi: {ex.Message}");
            }
        }

        // Thêm phương thức gửi cập nhật hàng đợi định kỳ
        public async Task SendQueueStatusPeriodically(CancellationToken cancellationToken)
        {
            var queueService = Context.GetHttpContext().RequestServices.GetService<QueueService>();
            if (queueService == null) return;
            
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var currentQueue = queueService.GetQueue();
                    var queueHistory = queueService.GetQueueHistory();
                    
                    await Clients.All.SendAsync("ReceiveQueueUpdate", 
                        new { CurrentQueue = currentQueue, QueueHistory = queueHistory }, 
                        cancellationToken);
                        
                    await Task.Delay(5000, cancellationToken); // Cập nhật mỗi 5 giây
                }
                catch (OperationCanceledException)
                {
                    // Bỏ qua khi bị hủy
                    break;
                }
                catch (Exception ex)
                {
                    // Xử lý lỗi nhưng không dừng vòng lặp
                    _logger.LogError(ex, "Lỗi khi gửi cập nhật hàng đợi định kỳ");
                    await Task.Delay(1000, cancellationToken); // Chờ 1 giây trước khi thử lại
                }
            }
        }
    }
}