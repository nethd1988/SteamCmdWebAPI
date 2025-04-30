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

        public QueueService(
            ILogger<QueueService> logger,
            ProfileService profileService,
            SteamApiService steamApiService,
            IHubContext<LogHub> hubContext,
            IServiceProvider serviceProvider) // Thêm IServiceProvider
        {
            _logger = logger;
            _profileService = profileService;
            _steamApiService = steamApiService;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider; // Lưu IServiceProvider

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

        public async Task<bool> AddToQueue(int profileId, string appId, bool isMainApp = true, string parentAppId = null)
        {
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

                lock (_queueLock)
                {
                    // Kiểm tra xem đã có trong hàng đợi chưa
                    if (_queue.Any(q => q.ProfileId == profileId && q.AppId == appId && q.Status == "Đang chờ"))
                    {
                        _logger.LogInformation("AppID {AppId} của Profile {ProfileName} đã có trong hàng đợi", appId, profile.Name);
                        return true;
                    }

                    _queue.Add(queueItem);
                    SaveQueueToFile();
                }

                _logger.LogInformation("Đã thêm {AppName} (AppID: {AppId}) của Profile {ProfileName} vào hàng đợi", appName, appId, profile.Name);

                // Nếu chưa đang xử lý, bắt đầu xử lý
                if (!_isProcessing)
                {
                    StartProcessing();
                }

                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi thêm AppID {AppId} của Profile {ProfileId} vào hàng đợi", appId, profileId);
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
            _logger.LogInformation("Bắt đầu xử lý hàng đợi cập nhật");
            await _hubContext.Clients.All.SendAsync("ReceiveLog", "Bắt đầu xử lý hàng đợi cập nhật");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    QueueItem currentItem = null;

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
                            SaveQueueToFile();
                        }
                    }

                    if (currentItem == null)
                    {
                        // Không còn mục nào để xử lý
                        _isProcessing = false;
                        break;
                    }

                    await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });

                    _logger.LogInformation("Đang xử lý cập nhật {AppName} (AppID: {AppId}) cho Profile {ProfileName}",
                        currentItem.AppName, currentItem.AppId, currentItem.ProfileName);

                    await _hubContext.Clients.All.SendAsync("ReceiveLog",
                        $"Đang xử lý cập nhật {currentItem.AppName} (AppID: {currentItem.AppId}) cho Profile {currentItem.ProfileName}");

                    bool success = false;

                    try
                    {
                        // Lấy SteamCmdService từ IServiceProvider
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var steamCmdService = scope.ServiceProvider.GetRequiredService<SteamCmdService>();

                            // Chạy cập nhật
                            success = await steamCmdService.RunSpecificAppAsync(currentItem.ProfileId, currentItem.AppId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi cập nhật {AppName} (AppID: {AppId}) cho Profile {ProfileName}",
                            currentItem.AppName, currentItem.AppId, currentItem.ProfileName);

                        await _hubContext.Clients.All.SendAsync("ReceiveLog",
                            $"Lỗi cập nhật {currentItem.AppName}: {ex.Message}");

                        // Cập nhật thông tin lỗi
                        lock (_queueLock)
                        {
                            currentItem.Error = ex.Message;
                        }
                    }

                    // Cập nhật trạng thái và di chuyển sang lịch sử
                    lock (_queueLock)
                    {
                        currentItem.Status = success ? "Hoàn thành" : "Lỗi";
                        currentItem.CompletedAt = DateTime.Now;

                        // Di chuyển từ hàng đợi sang lịch sử
                        _queue.Remove(currentItem);
                        _queueHistory.Insert(0, currentItem);

                        // Đảm bảo giới hạn kích thước lịch sử
                        if (_queueHistory.Count > _maxHistoryItems)
                        {
                            _queueHistory.RemoveRange(_maxHistoryItems, _queueHistory.Count - _maxHistoryItems);
                        }

                        SaveQueueToFile();
                    }

                    await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });

                    // Đợi 3 giây trước khi xử lý mục tiếp theo
                    await Task.Delay(3000, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Dừng xử lý do hủy
                _logger.LogInformation("Xử lý hàng đợi đã bị hủy");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý hàng đợi cập nhật");
            }
            finally
            {
                _isProcessing = false;
                _logger.LogInformation("Kết thúc xử lý hàng đợi");

                await _hubContext.Clients.All.SendAsync("ReceiveLog", "Đã hoàn thành xử lý hàng đợi cập nhật");
                await _hubContext.Clients.All.SendAsync("ReceiveQueueUpdate", new { CurrentQueue = GetQueue(), QueueHistory = GetQueueHistory() });
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
                            var allItems = JsonSerializer.Deserialize<List<QueueItem>>(json);

                            lock (_queueLock)
                            {
                                _queue = allItems?.Where(q => q.Status == "Đang chờ" || q.Status == "Đang xử lý").ToList() ?? new List<QueueItem>();
                                _queueHistory = allItems?.Where(q => q.Status != "Đang chờ" && q.Status != "Đang xử lý").ToList() ?? new List<QueueItem>();
                            }
                        }
                    }
                    catch
                    {
                        // Định dạng cũ (chỉ có queue)
                        var allItems = JsonSerializer.Deserialize<List<QueueItem>>(json);

                        lock (_queueLock)
                        {
                            _queue = allItems?.Where(q => q.Status == "Đang chờ" || q.Status == "Đang xử lý").ToList() ?? new List<QueueItem>();
                            _queueHistory = allItems?.Where(q => q.Status != "Đang chờ" && q.Status != "Đang xử lý").ToList() ?? new List<QueueItem>();
                        }
                    }

                    _logger.LogInformation("Đã tải {0} mục đang chờ và {1} mục lịch sử", _queue.Count, _queueHistory.Count);
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
            }
        }

        private void SaveQueueToFile()
        {
            try
            {
                var queueData = new
                {
                    Queue = _queue,
                    History = _queueHistory
                };

                string json = JsonSerializer.Serialize(queueData, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_queueFilePath, json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu hàng đợi vào file");
            }
        }
    }
}