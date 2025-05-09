using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SteamCmdWebAPI.Hubs;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    public class QueueService
    {
        private readonly ILogger<QueueService> _logger;
        private readonly ProfileService _profileService;
        private readonly SteamApiService _steamApiService;
        private readonly IHubContext<LogHub> _hubContext;
        private readonly IServiceProvider _serviceProvider; // Thêm IServiceProvider
        private readonly string _queueFilePath;
        private readonly object _queueLock = new object();
        private List<QueueItem> _queue = new List<QueueItem>();
        private List<QueueItem> _queueHistory = new List<QueueItem>();
        private readonly int _maxHistoryItems = 100;
        private bool _isProcessing = false;
        private CancellationTokenSource _cancellationTokenSource;

        // Thay đổi từ private class thành public class
        public class QueueData
        {
            public List<QueueItem> Queue { get; set; }
            public List<QueueItem> History { get; set; }
        }

        public class QueueItem
        {
            public int Id { get; set; }
            public int ProfileId { get; set; }
            public string ProfileName { get; set; }
            public string AppId { get; set; }
            public string AppName { get; set; }
            public string Status { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime? StartedAt { get; set; }
            public DateTime? CompletedAt { get; set; }
            public string Error { get; set; }
            public bool IsMainApp { get; set; }
            public string ParentAppId { get; set; }
            public int Order { get; set; }
            public TimeSpan? ProcessingTime { get; set; }
            public string ProcessingStatus { get; set; }
            public int RetryCount { get; set; }
            public DateTime? LastRetryTime { get; set; }
            public long DownloadedSize { get; set; }
            public long TotalSize { get; set; }
        }

        public QueueService(
            ILogger<QueueService> logger,
            ProfileService profileService,
            SteamApiService steamApiService,
            IHubContext<LogHub> hubContext,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _profileService = profileService;
            _steamApiService = steamApiService;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;

            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string dataDir = Path.Combine(baseDirectory, "data");
            if (!Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            _queueFilePath = Path.Combine(dataDir, "update_queue.json");

            // Tải hàng đợi từ file khi khởi động
            LoadQueueFromFile();

            // Khởi động bộ xử lý hàng đợi nếu có mục đang chờ
            if (_queue.Any(q => q.Status == "Đang chờ"))
            {
                StartProcessing();
            }
        }

        public List<QueueItem> GetQueue()
        {
            lock (_queueLock)
            {
                return _queue.Where(q => q.Status == "Đang chờ" || q.Status == "Đang xử lý")
                             .OrderBy(q => q.Order)
                             .ToList();
            }
        }

        public List<QueueItem> GetQueueHistory()
        {
            lock (_queueLock)
            {
                return _queueHistory.OrderByDescending(q => q.CompletedAt ?? q.CreatedAt).ToList();
            }
        }

        public bool IsAlreadyInQueue(int profileId, string appId)
        {
            lock (_queueLock)
            {
                // Kiểm tra cả trong danh sách đang chờ và đang xử lý
                return _queue.Any(q => q.ProfileId == profileId && 
                                      q.AppId == appId && 
                                      (q.Status == "Đang chờ" || q.Status == "Đang xử lý"));
            }
        }

        public async Task<bool> AddToQueue(int profileId, string appId, bool isMainApp = true, string parentAppId = null)
        {
            // Kiểm tra ngay từ đầu nếu đã có trong hàng đợi
            if (IsAlreadyInQueue(profileId, appId))
            {
                _logger.LogInformation("AddToQueue: AppID {AppId} đã có trong hàng đợi, bỏ qua", appId);
                return true;
            }

            try
            {
                var profile = await _profileService.GetProfileById(profileId);
                if (profile == null)
                {
                    _logger.LogWarning("Không tìm thấy profile ID {ProfileId} để thêm vào hàng đợi", profileId);
                    return false;
                }

                var appInfo = await _steamApiService.GetAppUpdateInfo(appId);
                string appName = appInfo?.Name ?? $"AppID {appId}";

                // Kiểm tra xem profile có quá nhiều app ID không
                var existingAppIds = _queue
                    .Where(q => q.ProfileId == profileId && q.Status == "Đang chờ")
                    .Select(q => q.AppId)
                    .ToList();
                
                if (existingAppIds.Count > 10) // Nếu có quá nhiều app ID trong queue
                {
                    _logger.LogWarning("Queue quá lớn cho profile {ProfileId}, xử lý theo lô", profileId);
                    await _hubContext.Clients.All.SendAsync("ReceiveLog", $"Queue quá lớn cho profile {profile.Name}, xử lý theo lô");
                }

                var queueItem = new QueueItem
                {
                    Id = DateTime.Now.Ticks.GetHashCode(),
                    ProfileId = profileId,
                    ProfileName = profile.Name,
                    AppId = appId,
                    AppName = appName,
                    Status = "Đang chờ",
                    CreatedAt = DateTime.Now,
                    IsMainApp = isMainApp,
                    ParentAppId = parentAppId,
                    Order = GetNextOrder()
                };

                // Lấy thông tin kích thước từ SteamApiService nếu có thể
                try
                {
                    if (appInfo != null)
                    {
                        // Ưu tiên sử dụng UpdateSize nếu có
                        if (appInfo.UpdateSize > 0)
                        {
                            queueItem.TotalSize = appInfo.UpdateSize;
                            _logger.LogInformation("Đã lấy thông tin kích thước cập nhật cho AppID {0}: {1} bytes", appId, appInfo.UpdateSize);
                        }
                        // Nếu không có UpdateSize, sử dụng SizeOnDisk
                        else if (appInfo.SizeOnDisk > 0)
                        {
                            queueItem.TotalSize = appInfo.SizeOnDisk;
                            _logger.LogInformation("Không có thông tin kích thước cập nhật, sử dụng kích thước tổng cho AppID {0}: {1} bytes", appId, appInfo.SizeOnDisk);
                        }
                        else
                        {
                            _logger.LogDebug("Không có thông tin kích thước từ SteamApiService cho AppID {0}", appId);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Không có thông tin AppInfo từ SteamApiService cho AppID {0}", appId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Không thể lấy thông tin kích thước từ SteamApiService cho AppID {0}", appId);
                }

                bool added = false;
                lock (_queueLock)
                {
                    // Kiểm tra lại một lần nữa trong trường hợp đã có thêm vào khi đang xử lý
                    if (!IsAlreadyInQueue(profileId, appId))
                    {
                        _queue.Add(queueItem);
                        added = true;
                        try {
                            SaveQueueToFile();
                        }
                        catch (Exception ex) {
                            _logger.LogError(ex, "Lỗi khi lưu hàng đợi vào file");
                        }
                    }
                }

                if (added)
                {
                    _logger.LogInformation("Đã thêm {AppName} (AppID: {AppId}) của Profile {ProfileName} vào hàng đợi", 
                        appName, appId, profile.Name);
                        
                    // Gửi ngay thông báo cập nhật hàng đợi
                    try {
                        await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                            new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
                    }
                    catch (Exception ex) {
                        _logger.LogError(ex, "Lỗi khi gửi thông báo ReceiveQueueUpdate");
                    }

                    // Nếu chưa đang xử lý, bắt đầu xử lý
                    if (!_isProcessing)
                    {
                        StartProcessing();
                    }
                }
                else
                {
                    _logger.LogInformation("Bỏ qua thêm {AppName} (AppID: {AppId}) vì đã có trong hàng đợi", appName, appId);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm AppID {AppId} vào hàng đợi: {Error}", appId, ex.Message);
                return false;
            }
        }

        public async Task<bool> RemoveFromQueue(int queueItemId)
        {
            lock (_queueLock)
            {
                var item = _queue.FirstOrDefault(q => q.Id == queueItemId && q.Status == "Đang chờ");
                if (item == null)
                {
                    return false;
                }

                _queue.Remove(item);
                SaveQueueToFile();
            }

            await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
            return true;
        }

        public async Task ClearQueue()
        {
            lock (_queueLock)
            {
                _queue.RemoveAll(q => q.Status == "Đang chờ");
                SaveQueueToFile();
            }

            await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
        }

        public void StartProcessing()
        {
            if (_isProcessing)
            {
                return;
            }

            _isProcessing = true;
            _cancellationTokenSource = new CancellationTokenSource();

            Task.Run(async () => await ProcessQueueAsync(_cancellationTokenSource.Token));
        }

        public async Task StopProcessing()
        {
            if (!_isProcessing)
            {
                return;
            }

            _cancellationTokenSource?.Cancel();
            _isProcessing = false;

            // Cập nhật trạng thái các mục đang chờ
            lock (_queueLock)
            {
                foreach (var item in _queue.Where(q => q.Status == "Đang chờ" || q.Status == "Đang xử lý"))
                {
                    item.Status = "Đã hủy";
                    item.CompletedAt = DateTime.Now;

                    // Di chuyển từ hàng đợi sang lịch sử
                    _queue.Remove(item);
                    _queueHistory.Insert(0, item);
                }

                // Đảm bảo giới hạn kích thước lịch sử
                if (_queueHistory.Count > _maxHistoryItems)
                {
                    _queueHistory.RemoveRange(_maxHistoryItems, _queueHistory.Count - _maxHistoryItems);
                }

                SaveQueueToFile();
            }

            await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    QueueItem currentItem = null;

                    // Lấy mục tiếp theo cần xử lý
                    lock (_queueLock)
                    {
                        currentItem = _queue
                            .Where(q => q.Status == "Đang chờ")
                            .OrderBy(q => q.Order)
                            .FirstOrDefault();

                        if (currentItem != null)
                        {
                            currentItem.Status = "Đang xử lý";
                            currentItem.StartedAt = DateTime.Now;
                            currentItem.ProcessingStatus = "Đang khởi động";
                            SaveQueueToFile();
                        }
                    }

                    if (currentItem == null)
                    {
                        // Không còn mục cần xử lý, thoát vòng lặp
                        break;
                    }

                    // Thông báo ngay cập nhật trạng thái
                    await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                        new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
                    
                    _logger.LogInformation("Đang xử lý cập nhật {0} (AppID: {1}) cho Profile {2}",
                        currentItem.AppName, currentItem.AppId, currentItem.ProfileName);

                    // QUAN TRỌNG: Đánh dấu thời gian bắt đầu xử lý
                    DateTime startTime = DateTime.Now;
                    bool success = false;

                    try
                    {
                        // Đảm bảo các tiến trình SteamCMD cũ đã dừng trước khi bắt đầu mục mới
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var steamCmdService = scope.ServiceProvider.GetRequiredService<SteamCmdService>();
                            await steamCmdService.KillAllSteamCmdProcessesAsync();
                            await Task.Delay(2000); // Đợi 2 giây để đảm bảo tiến trình đã dừng hoàn toàn
                        }
                        
                        // Sử dụng SteamCmdService để cập nhật
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var steamCmdService = scope.ServiceProvider.GetRequiredService<SteamCmdService>();
                            var profile = await _profileService.GetProfileById(currentItem.ProfileId);
                            if (profile != null)
                            {
                                currentItem.ProcessingStatus = "Đang chạy SteamCMD";
                                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                                    new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });

                                var result = await steamCmdService.ExecuteProfileUpdateAsync(currentItem.ProfileId, currentItem.AppId);
                                success = result;
                                
                                // Thêm kiểm tra bổ sung nếu ExecuteProfileUpdateAsync trả về false
                                if (!success)
                                {
                                    // Kiểm tra trong LogService có log thành công cho AppID này không
                                    using (var logScope = _serviceProvider.CreateScope())
                                    {
                                        var logService = logScope.ServiceProvider.GetRequiredService<LogService>();
                                        var recentLogs = logService.GetRecentLogs(50);
                                        
                                        // Kiểm tra nếu có bất kỳ log Success nào chứa AppID này
                                        var successLogs = recentLogs.Where(log => 
                                            log.Level.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) &&
                                            (log.Message.Contains($"App '{currentItem.AppId}'") || 
                                             log.Message.Contains($"AppID: {currentItem.AppId}") ||
                                             log.Message.Contains($"fully installed") && log.Message.Contains(currentItem.AppId) ||
                                             log.Message.Contains($"already up to date") && log.Message.Contains(currentItem.AppId)));
                                        
                                        if (successLogs.Any())
                                        {
                                            _logger.LogInformation("Phát hiện log thành công cho {0} (AppID: {1}) trong LogService mặc dù ExecuteProfileUpdateAsync trả về false", 
                                                currentItem.AppName, currentItem.AppId);
                                            success = true;
                                            currentItem.Error = null;
                                        }
                                        else 
                                        {
                                            // Phân tích loại lỗi và quyết định thử lại nếu cần
                                            var errorLogs = logService.GetRecentLogs(100)
                                                .Where(log => (log.Level.Equals("ERROR", StringComparison.OrdinalIgnoreCase) || 
                                                              log.Status.Equals("Error", StringComparison.OrdinalIgnoreCase)) &&
                                                              (log.Message.Contains(currentItem.AppId) || 
                                                               log.Message.Contains("Invalid Password") ||
                                                               log.Message.Contains("No subscription") ||
                                                               log.Message.Contains("Error! App") ||
                                                               log.Message.Contains("state is 0x") ||
                                                               log.Message.Contains("Rate Limit Exceeded") ||
                                                               log.Message.Contains("Sai tên đăng nhập")))
                                                .Take(10)
                                                .ToList();
                                                
                                            bool shouldRetry = false;
                                            bool shouldUseAltAccount = false;
                                            bool shouldValidate = false;
                                            string errorReason = "Lỗi không xác định";
                                            
                                            // Phân tích loại lỗi
                                            foreach (var log in errorLogs)
                                            {
                                                string message = log.Message;
                                                
                                                // Phát hiện lỗi đăng nhập
                                                if (message.Contains("Invalid Password") || 
                                                    message.Contains("Sai tên đăng nhập") || 
                                                    message.Contains("Rate Limit Exceeded"))
                                                {
                                                    shouldRetry = true;
                                                    shouldUseAltAccount = true;
                                                    errorReason = "Lỗi đăng nhập Steam";
                                                    break;
                                                }
                                                
                                                // Phát hiện lỗi No subscription
                                                if (message.Contains("No subscription") || message.Contains("không có quyền truy cập"))
                                                {
                                                    shouldRetry = true;
                                                    shouldUseAltAccount = true;
                                                    errorReason = "Tài khoản không có quyền truy cập ứng dụng";
                                                    break;
                                                }
                                                
                                                // Phát hiện lỗi state 0x606 hoặc tương tự
                                                if (message.Contains("state is 0x") || 
                                                    message.Contains("Error! App") && message.Contains("state"))
                                                {
                                                    shouldRetry = true;
                                                    shouldValidate = true;
                                                    errorReason = "Lỗi trạng thái cập nhật, cần validate";
                                                    break;
                                                }
                                            }
                                            
                                            // Quyết định thử lại
                                            if (shouldRetry && currentItem.RetryCount < 3)
                                            {
                                                currentItem.RetryCount++;
                                                currentItem.LastRetryTime = DateTime.Now;
                                                currentItem.Status = "Đang chờ";
                                                currentItem.Error = $"{errorReason}. Đang thử lại lần {currentItem.RetryCount}/3";
                                                currentItem.ProcessingStatus = "Đang chuẩn bị thử lại";
                                                
                                                // Ghi log
                                                _logger.LogInformation(
                                                    "Lên lịch thử lại cho {0} (AppID: {1}). Lần thử: {2}/3. Lý do: {3}. UseAltAccount: {4}, Validate: {5}", 
                                                    currentItem.AppName, currentItem.AppId, currentItem.RetryCount, errorReason, 
                                                    shouldUseAltAccount, shouldValidate);
                                                
                                                await _hubContext.Clients.All.SendAsync("ReceiveLog", 
                                                    $"Lên lịch thử lại cho {currentItem.AppName} (AppID: {currentItem.AppId}). Lý do: {errorReason}");
                                                
                                                // Thông báo cho SteamCmdService về việc cần thử lại với validate hoặc tài khoản khác
                                                if (shouldUseAltAccount || shouldValidate)
                                                {
                                                    using (var retryScope = _serviceProvider.CreateScope())
                                                    {
                                                        var steamCmdSvc = retryScope.ServiceProvider.GetRequiredService<SteamCmdService>();
                                                        
                                                        if (shouldValidate)
                                                        {
                                                            await _hubContext.Clients.All.SendAsync("ReceiveLog", 
                                                                $"Thử lại với validate=true cho {currentItem.AppName} (AppID: {currentItem.AppId})");
                                                                
                                                            // Đánh dấu appId với tiền tố validate: để thử lại với validate=true
                                                            steamCmdSvc.AddAppToRetryList($"validate:{currentItem.AppId}", currentItem.ProfileId);
                                                        }
                                                        else if (shouldUseAltAccount)
                                                        {
                                                            await _hubContext.Clients.All.SendAsync("ReceiveLog", 
                                                                $"Thử lại với tài khoản khác cho {currentItem.AppName} (AppID: {currentItem.AppId})");
                                                                
                                                            // Đánh dấu appId để thử lại với tài khoản khác
                                                            steamCmdSvc.AddAppToRetryList(currentItem.AppId, currentItem.ProfileId);
                                                        }
                                                    }
                                                }
                                                
                                                lock (_queueLock)
                                                {
                                                    // Đưa lại vào hàng đợi chờ với order cao hơn để ưu tiên thấp hơn các mục mới
                                                    currentItem.Order = GetNextOrder() + 100;
                                                    SaveQueueToFile();
                                                }
                                                
                                                // Skip updating queue history since we're retrying
                                                continue;
                                            }
                                            else if (currentItem.RetryCount >= 3)
                                            {
                                                // Đã thử lại quá nhiều lần
                                                currentItem.Error = $"{errorReason}. Đã thử lại {currentItem.RetryCount} lần nhưng không thành công.";
                                                _logger.LogWarning("Đã thử lại {0} lần cho {1} (AppID: {2}) nhưng vẫn thất bại. Lý do: {3}", 
                                                    currentItem.RetryCount, currentItem.AppName, currentItem.AppId, errorReason);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        
                        // QUAN TRỌNG: Kiểm tra nếu xử lý quá nhanh (dưới 10 giây) - có thể có vấn đề
                        TimeSpan processingTime = DateTime.Now - startTime;
                        currentItem.ProcessingTime = processingTime;
                        
                        if (processingTime.TotalSeconds < 10 && !success)
                        {
                            _logger.LogWarning("ProcessQueueAsync: Xử lý quá nhanh ({0} giây) cho {1} (AppID: {2}), đánh dấu không thành công",
                                processingTime.TotalSeconds, currentItem.AppName, currentItem.AppId);
                            
                            success = false;
                            currentItem.Error = "Xử lý quá nhanh, có thể SteamCMD không chạy đúng cách";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi cập nhật {0} (AppID: {1})", currentItem.AppName, currentItem.AppId);
                        currentItem.Error = ex.Message;
                        currentItem.ProcessingStatus = "Lỗi: " + ex.Message;
                    }

                    // Cập nhật trạng thái và lưu lịch sử
                    lock (_queueLock)
                    {
                        currentItem.Status = success ? "Hoàn thành" : "Lỗi";
                        currentItem.CompletedAt = DateTime.Now;
                        currentItem.ProcessingStatus = success ? "Hoàn thành" : "Thất bại";

                        _queue.Remove(currentItem);
                        _queueHistory.Insert(0, currentItem);

                        if (_queueHistory.Count > _maxHistoryItems)
                        {
                            _queueHistory.RemoveRange(_maxHistoryItems, _queueHistory.Count - _maxHistoryItems);
                        }

                        SaveQueueToFile();
                    }

                    // QUAN TRỌNG: Thông báo ngay lập tức
                    await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                        new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });

                    // Thông báo log
                    if (success)
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", 
                            $"Đã hoàn thành cập nhật {currentItem.AppName} (AppID: {currentItem.AppId}) trong {currentItem.ProcessingTime?.TotalSeconds:F1} giây");
                    }
                    else
                    {
                        await _hubContext.Clients.All.SendAsync("ReceiveLog", 
                            $"Lỗi khi cập nhật {currentItem.AppName} (AppID: {currentItem.AppId}): {currentItem.Error}");
                    }

                    // Chờ một chút trước khi xử lý mục tiếp theo
                    await Task.Delay(1000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Bỏ qua khi bị hủy
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý hàng đợi cập nhật");
            }
            finally
            {
                _isProcessing = false;
                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã hoàn thành xử lý hàng đợi cập nhật");
                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                    new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
            }
        }

        private int GetNextOrder()
        {
            lock (_queueLock)
            {
                return _queue.Any() ? _queue.Max(q => q.Order) + 1 : 1;
            }
        }

        private void LoadQueueFromFile()
        {
            try
            {
                if (File.Exists(_queueFilePath))
                {
                    string json = File.ReadAllText(_queueFilePath);
                    
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _logger.LogWarning("File hàng đợi rỗng, khởi tạo mới.");
                        _queue = new List<QueueItem>();
                        _queueHistory = new List<QueueItem>();
                        SaveQueueToFile(); // Tạo file mới với cấu trúc đúng
                        return;
                    }

                    // Thử đọc theo định dạng mới (queue + history)
                    try
                    {
                        var jsonDoc = JsonDocument.Parse(json);
                        var root = jsonDoc.RootElement;

                        if (root.TryGetProperty("Queue", out var queueElement) &&
                            root.TryGetProperty("History", out var historyElement))
                        {
                            var queueItems = JsonSerializer.Deserialize<List<QueueItem>>(queueElement.GetRawText());
                            var historyItems = JsonSerializer.Deserialize<List<QueueItem>>(historyElement.GetRawText());

                            lock (_queueLock)
                            {
                                _queue = queueItems ?? new List<QueueItem>();
                                _queueHistory = historyItems ?? new List<QueueItem>();
                            }
                        }
                        else
                        {
                            // Định dạng cũ (chỉ có queue)
                            try
                            {
                                var allItems = JsonSerializer.Deserialize<List<QueueItem>>(json);

                                lock (_queueLock)
                                {
                                    _queue = allItems?.Where(q => q.Status == "Đang chờ" || q.Status == "Đang xử lý").ToList() ?? new List<QueueItem>();
                                    _queueHistory = allItems?.Where(q => q.Status != "Đang chờ" && q.Status != "Đang xử lý").ToList() ?? new List<QueueItem>();
                                }
                            }
                            catch (Exception innerEx)
                            {
                                _logger.LogError(innerEx, "Lỗi khi phân tích file hàng đợi. Tạo mới file.");
                                lock (_queueLock)
                                {
                                    _queue = new List<QueueItem>();
                                    _queueHistory = new List<QueueItem>();
                                }
                                // Tạo file mới với cấu trúc đúng
                                SaveQueueToFile();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi parse JSON. Tạo mới file hàng đợi.");
                        // Định dạng cũ (chỉ có queue)
                        try
                        {
                            var allItems = JsonSerializer.Deserialize<List<QueueItem>>(json);

                            lock (_queueLock)
                            {
                                _queue = allItems?.Where(q => q.Status == "Đang chờ" || q.Status == "Đang xử lý").ToList() ?? new List<QueueItem>();
                                _queueHistory = allItems?.Where(q => q.Status != "Đang chờ" && q.Status != "Đang xử lý").ToList() ?? new List<QueueItem>();
                            }
                        }
                        catch
                        {
                            _logger.LogError("File hàng đợi bị hỏng. Tạo mới file.");
                            lock (_queueLock)
                            {
                                _queue = new List<QueueItem>();
                                _queueHistory = new List<QueueItem>();
                            }
                            // Tạo file mới với cấu trúc đúng
                            SaveQueueToFile();
                        }
                    }

                    _logger.LogInformation("Đã tải {0} mục đang chờ và {1} mục lịch sử", _queue.Count, _queueHistory.Count);
                }
                else
                {
                    _logger.LogInformation("Không tìm thấy file hàng đợi. Tạo file mới.");
                    lock (_queueLock)
                    {
                        _queue = new List<QueueItem>();
                        _queueHistory = new List<QueueItem>();
                    }
                    // Tạo file mới
                    SaveQueueToFile();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi tải hàng đợi từ file");
                lock (_queueLock)
                {
                    _queue = new List<QueueItem>();
                    _queueHistory = new List<QueueItem>();
                }
                // Tạo file mới nếu có lỗi
                try {
                    SaveQueueToFile();
                }
                catch (Exception saveEx) {
                    _logger.LogError(saveEx, "Không thể tạo file hàng đợi mới");
                }
            }
        }

        private void SaveQueueToFile()
        {
            try
            {
                string directory = Path.GetDirectoryName(_queueFilePath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var queueData = new QueueData
                {
                    Queue = _queue,
                    History = _queueHistory
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                string json = JsonSerializer.Serialize(queueData, options);
                File.WriteAllText(_queueFilePath, json);
                _logger.LogDebug("Đã lưu hàng đợi vào file thành công");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu hàng đợi vào file");
            }
        }

        // Thêm phương thức để lấy danh sách hàng đợi trực tiếp từ file
        public async Task<(List<QueueItem> Queue, List<QueueItem> History)> LoadQueueFromFileAsync()
        {
            try
            {
                if (!File.Exists(_queueFilePath))
                {
                    return (new List<QueueItem>(), new List<QueueItem>());
                }

                string json = await File.ReadAllTextAsync(_queueFilePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return (new List<QueueItem>(), new List<QueueItem>());
                }

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                try
                {
                    var data = JsonSerializer.Deserialize<QueueData>(json, options);
                    return (data.Queue ?? new List<QueueItem>(), data.History ?? new List<QueueItem>());
                }
                catch
                {
                    // Thử đọc theo định dạng cũ
                    var allItems = JsonSerializer.Deserialize<List<QueueItem>>(json, options);
                    var queue = allItems?.Where(q => q.Status == "Đang chờ" || q.Status == "Đang xử lý").ToList() ?? new List<QueueItem>();
                    var history = allItems?.Where(q => q.Status != "Đang chờ" && q.Status != "Đang xử lý").ToList() ?? new List<QueueItem>();
                    
                    return (queue, history);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc hàng đợi từ file");
                return (new List<QueueItem>(), new List<QueueItem>());
            }
        }

        public async Task<QueueData> LoadQueueDataAsync()
        {
            var (queue, history) = await LoadQueueFromFileAsync();
            return new QueueData { Queue = queue, History = history };
        }

        // Thêm phương thức cập nhật trạng thái hàng đợi và gửi thông báo
        public async Task UpdateQueueStatusAsync()
        {
            try
            {
                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                    new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật trạng thái hàng đợi");
            }
        }

        // Phương thức mới để cập nhật thông tin tiến trình tải của một QueueItem
        public async Task<bool> UpdateQueueItemProgress(int queueItemId, long downloadedSize, long totalSize)
        {
            try
            {
                lock (_queueLock)
                {
                    var item = _queue.FirstOrDefault(q => q.Id == queueItemId);
                    if (item == null)
                    {
                        _logger.LogWarning("UpdateQueueItemProgress: Không tìm thấy QueueItem với ID {Id}", queueItemId);
                        return false;
                    }

                    // Cập nhật thông tin
                    item.DownloadedSize = downloadedSize;
                    
                    // Chỉ cập nhật TotalSize nếu giá trị mới lớn hơn giá trị hiện tại
                    if (totalSize > item.TotalSize)
                    {
                        item.TotalSize = totalSize;
                    }
                    
                    // Lưu vào file
                    SaveQueueToFile();
                    
                    _logger.LogDebug("Đã cập nhật tiến trình tải cho QueueItem {Id}: DownloadedSize={Downloaded}, TotalSize={Total}",
                        queueItemId, downloadedSize, item.TotalSize);
                }

                // Thông báo cập nhật để cập nhật UI
                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                    new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
                
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi cập nhật tiến trình tải cho QueueItem {Id}", queueItemId);
                return false;
            }
        }
    }
}