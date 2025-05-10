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

        // Add a list to keep recent logs
        private readonly List<string> _recentLogs = new List<string>();
        private readonly object _logLock = new object();
        private readonly int _maxLogItems = 200;

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
                _logger.LogInformation("Bắt đầu xử lý hàng đợi");
                AddLog("Bắt đầu xử lý hàng đợi cập nhật");

                while (!cancellationToken.IsCancellationRequested)
                {
                    QueueItem nextItem = null;

                    lock (_queueLock)
                    {
                        // Lấy mục tiếp theo cần xử lý
                        nextItem = _queue.FirstOrDefault(q => q.Status == "Đang chờ");
                        
                        if (nextItem != null)
                        {
                            nextItem.Status = "Đang xử lý";
                            nextItem.StartedAt = DateTime.Now;
                            nextItem.ProcessingStatus = "Đang chuẩn bị";
                            SaveQueueToFile();
                        }
                    }

                    if (nextItem == null)
                    {
                        // Không còn mục nào để xử lý
                        _isProcessing = false;
                        _logger.LogInformation("Không còn mục nào trong hàng đợi, dừng xử lý");
                        AddLog("Đã hoàn thành tất cả các mục trong hàng đợi");
                        break;
                    }

                    try
                    {
                        _logger.LogInformation("Bắt đầu cập nhật {AppName} (AppID: {AppId})", 
                            nextItem.AppName, nextItem.AppId);
                        AddLog($"[{DateTime.Now:HH:mm:ss}] Bắt đầu cập nhật {nextItem.AppName} (AppID: {nextItem.AppId})");

                        // Gửi thông báo cập nhật hàng đợi sau khi đã cập nhật trạng thái
                    await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                            new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() }, 
                            cancellationToken);

                        // Lấy profile
                        var profile = await _profileService.GetProfileById(nextItem.ProfileId);
                        if (profile == null)
                        {
                            throw new Exception($"Không tìm thấy profile ID {nextItem.ProfileId}");
                        }

                        // Thực hiện cập nhật
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var steamCmdService = scope.ServiceProvider.GetRequiredService<SteamCmdService>();
                            
                            nextItem.ProcessingStatus = "Đang cập nhật";
                            
                            // Thực hiện cập nhật app
                            bool updateSuccess = false;
                            try
                            {
                                // Đảm bảo tài khoản đăng nhập đúng
                                string steamUsername = null;
                                string steamPassword = null;
                                
                                // Giải mã thông tin đăng nhập nếu cần
                                var encryptionService = scope.ServiceProvider.GetRequiredService<EncryptionService>();
                                
                                if (!string.IsNullOrEmpty(profile.SteamUsername))
                                {
                                    try
                                    {
                                        steamUsername = encryptionService.Decrypt(profile.SteamUsername);
                                        _logger.LogInformation("Đã giải mã tên đăng nhập cho profile {ProfileName}", profile.Name);
                                        AddLog($"Đã giải mã tên đăng nhập cho profile {profile.Name}");
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Lỗi khi giải mã tên đăng nhập Steam");
                                        AddLog($"Lỗi khi giải mã tên đăng nhập Steam: {ex.Message}");
                                    }
                                }
                                
                                if (!string.IsNullOrEmpty(profile.SteamPassword))
                                {
                                    try
                                    {
                                        steamPassword = encryptionService.Decrypt(profile.SteamPassword);
                                        _logger.LogInformation("Đã giải mã mật khẩu cho profile {ProfileName}", profile.Name);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Lỗi khi giải mã mật khẩu Steam");
                                        AddLog($"Lỗi khi giải mã mật khẩu Steam: {ex.Message}");
                                    }
                                }
                                
                                // Bước 1: Cài đặt app
                                _logger.LogInformation("Thực hiện cập nhật cho {AppName} (AppID: {AppId}) với profile {ProfileName}",
                                    nextItem.AppName, nextItem.AppId, profile.Name);
                                AddLog($"Thực hiện cập nhật cho {nextItem.AppName} (AppID: {nextItem.AppId}) với profile {profile.Name}");
                                
                                updateSuccess = await steamCmdService.ExecuteProfileUpdateAsync(
                                    profile.Id,
                                    nextItem.AppId);
                                    
                                if (updateSuccess)
                                {
                                    _logger.LogInformation("Cập nhật thành công {AppName} (AppID: {AppId})", 
                                        nextItem.AppName, nextItem.AppId);
                                    AddLog($"Cập nhật thành công {nextItem.AppName} (AppID: {nextItem.AppId})");
                                    
                                    // Cập nhật trạng thái
                                    lock (_queueLock)
                                    {
                                        nextItem.Status = "Hoàn thành";
                                        nextItem.CompletedAt = DateTime.Now;
                                        nextItem.ProcessingTime = nextItem.CompletedAt - nextItem.StartedAt;
                                        nextItem.ProcessingStatus = "Hoàn thành";
                                        SaveQueueToFile();
                                        
                                        // Di chuyển vào lịch sử
                                        _queue.Remove(nextItem);
                                        _queueHistory.Add(nextItem);
                                        
                                        // Giới hạn kích thước lịch sử
                                        if (_queueHistory.Count > _maxHistoryItems)
                                        {
                                            _queueHistory = _queueHistory
                                                .OrderByDescending(h => h.CompletedAt ?? h.CreatedAt)
                                                .Take(_maxHistoryItems)
                                                .ToList();
                                        }
                                        SaveQueueToFile();
                                    }
                                    
                                    // Cập nhật thông tin cache cho app
                                    try
                                    {
                                        var steamApiService = scope.ServiceProvider.GetRequiredService<SteamApiService>();
                                        await steamApiService.GetAppUpdateInfo(nextItem.AppId, true);
                                        _logger.LogInformation("Đã cập nhật thông tin cache cho {AppName} (AppID: {AppId})", 
                                            nextItem.AppName, nextItem.AppId);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "Lỗi khi cập nhật thông tin cache cho AppID {AppId}", nextItem.AppId);
                                        AddLog($"Lỗi khi cập nhật thông tin cache cho AppID {nextItem.AppId}: {ex.Message}");
                                    }
                                    
                                    // Gửi thông báo cập nhật hàng đợi
                                    await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                                        new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() }, 
                                        cancellationToken);
                                }
                                else
                                {
                                    throw new Exception("Cập nhật không thành công, xem log để biết chi tiết");
                                }
                            }
                            catch (Exception ex)
                            {
                                // Xử lý lỗi
                                _logger.LogError(ex, "Lỗi khi cập nhật {AppName} (AppID: {AppId}): {Error}", 
                                    nextItem.AppName, nextItem.AppId, ex.Message);
                                AddLog($"Lỗi khi cập nhật {nextItem.AppName} (AppID: {nextItem.AppId}): {ex.Message}");
                                                
                                                lock (_queueLock)
                                                {
                                    nextItem.Status = "Lỗi";
                                    nextItem.Error = ex.Message;
                                    nextItem.CompletedAt = DateTime.Now;
                                    nextItem.ProcessingTime = nextItem.CompletedAt - nextItem.StartedAt;
                                    nextItem.ProcessingStatus = "Lỗi";
                                                    SaveQueueToFile();
                                    
                                    // Di chuyển vào lịch sử
                                    _queue.Remove(nextItem);
                                    _queueHistory.Add(nextItem);
                                    
                                    // Giới hạn kích thước lịch sử
                                    if (_queueHistory.Count > _maxHistoryItems)
                                    {
                                        _queueHistory = _queueHistory
                                            .OrderByDescending(h => h.CompletedAt ?? h.CreatedAt)
                                            .Take(_maxHistoryItems)
                                            .ToList();
                                    }
                                    SaveQueueToFile();
                                }
                                
                                // Gửi thông báo cập nhật hàng đợi
                                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                                    new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() }, 
                                    cancellationToken);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi nghiêm trọng khi xử lý mục hàng đợi {QueueItemId}: {Error}", 
                            nextItem.Id, ex.Message);
                        AddLog($"Lỗi nghiêm trọng khi xử lý mục hàng đợi {nextItem.Id}: {ex.Message}");
                        
                    lock (_queueLock)
                    {
                            nextItem.Status = "Lỗi";
                            nextItem.Error = ex.Message;
                            nextItem.CompletedAt = DateTime.Now;
                            nextItem.ProcessingStatus = "Lỗi";
                            SaveQueueToFile();
                            
                            // Di chuyển vào lịch sử
                            _queue.Remove(nextItem);
                            _queueHistory.Add(nextItem);
                            
                            // Giới hạn kích thước lịch sử
                        if (_queueHistory.Count > _maxHistoryItems)
                        {
                                _queueHistory = _queueHistory
                                    .OrderByDescending(h => h.CompletedAt ?? h.CreatedAt)
                                    .Take(_maxHistoryItems)
                                    .ToList();
                        }
                        SaveQueueToFile();
                    }

                        // Gửi thông báo cập nhật hàng đợi
                    await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                            new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() }, 
                            cancellationToken);
                    }
                    
                    // Kiểm tra nếu đã bị cancel
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Dừng xử lý hàng đợi theo yêu cầu.");
                        AddLog("Dừng xử lý hàng đợi theo yêu cầu người dùng.");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Đã hủy xử lý hàng đợi");
                AddLog("Đã hủy xử lý hàng đợi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý hàng đợi: {Error}", ex.Message);
                AddLog($"Lỗi khi xử lý hàng đợi: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
                
                // Gửi thông báo cập nhật hàng đợi cuối cùng
                try
                {
                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", 
                    new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lỗi khi gửi thông báo cập nhật hàng đợi cuối cùng: {Error}", ex.Message);
                }
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

        // Add log method
        public void AddLog(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
                
            // Ensure message is properly formatted and not an object
            string formattedMessage = message;
            
            // Remove any potential serialization issues
            if (message.Contains("{") && message.Contains("}"))
            {
                // If it looks like JSON, ensure it's just the message part
                try
                {
                    var doc = JsonDocument.Parse(message);
                    if (doc.RootElement.TryGetProperty("message", out var msgProperty))
                    {
                        formattedMessage = msgProperty.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("log", out var logProperty))
                    {
                        formattedMessage = logProperty.GetString();
                    }
                }
                catch
                {
                    // If parsing fails, keep original message
                }
            }
                
            lock (_logLock)
            {
                // Add timestamp if not already present
                if (!formattedMessage.StartsWith("[") && !char.IsDigit(formattedMessage[0]))
                {
                    formattedMessage = $"[{DateTime.Now.ToString("HH:mm:ss")}] {formattedMessage}";
                }
                
                _recentLogs.Add(formattedMessage);
                
                // Keep log size manageable
                if (_recentLogs.Count > _maxLogItems)
                {
                    _recentLogs.RemoveAt(0);
                }
                
                // Log to console as well
                _logger.LogInformation(formattedMessage);
            }
            
            // Send to clients via SignalR - ALWAYS send just the string
            try
            {
                var sendTask = _hubContext.Clients.All.SendAsync("ReceiveLog", formattedMessage);
                sendTask.Wait(TimeSpan.FromSeconds(1)); // Avoid indefinite wait
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending log via SignalR: {Message}", ex.Message);
            }
        }
        
        // Get recent logs
        public List<string> GetRecentLogs(int count = 100)
        {
            lock (_logLock)
            {
                // Return the most recent logs up to count
                return _recentLogs
                    .Skip(Math.Max(0, _recentLogs.Count - count))
                    .Take(Math.Min(_recentLogs.Count, count))
                    .ToList();
            }
        }
    }
}