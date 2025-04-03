using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Collections.Concurrent;
using System.Threading;
using System.IO;
using System.Text.Json;
using SteamCmdWebAPI.Models;

namespace SteamCmdWebAPI.Services
{
    /// <summary>
    /// Dịch vụ TCP Client tối ưu hóa cho việc giao tiếp với server SteamCMD.
    /// Sử dụng connection pooling, caching và xử lý hiệu suất cao.
    /// </summary>
    public class TcpClientService : IDisposable
    {
        private readonly ILogger<TcpClientService> _logger;
        private readonly EncryptionService _encryptionService;

        // Các hằng số
        private const string AUTH_TOKEN = "simple_auth_token";
        private const int DEFAULT_TIMEOUT = 5000; // 5 giây
        private const int CONNECTION_RETRY = 3;
        private const int BUFFER_SIZE = 8192; // 8KB buffer
        private const int MAX_CONCURRENT_REQUESTS = 5;

        // Connection pool để tái sử dụng kết nối
        private readonly ConcurrentDictionary<string, PooledTcpClient> _connectionPool =
            new ConcurrentDictionary<string, PooledTcpClient>();

        // Semaphore để giới hạn số lượng kết nối đồng thời
        private readonly SemaphoreSlim _connectionSemaphore =
            new SemaphoreSlim(MAX_CONCURRENT_REQUESTS, MAX_CONCURRENT_REQUESTS);

        // Cache cho việc lấy danh sách profiles
        private readonly ConcurrentDictionary<string, CachedItem<List<string>>> _profileNamesCache =
            new ConcurrentDictionary<string, CachedItem<List<string>>>();

        // Cache cho các profile details
        private readonly ConcurrentDictionary<string, CachedItem<SteamCmdProfile>> _profileDetailsCache =
            new ConcurrentDictionary<string, CachedItem<SteamCmdProfile>>();

        // Semaphore để giới hạn số lượng requests đồng thời cho mỗi máy chủ
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _serverSemaphores =
            new ConcurrentDictionary<string, SemaphoreSlim>();

        public TcpClientService(ILogger<TcpClientService> logger, EncryptionService encryptionService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _encryptionService = encryptionService ?? throw new ArgumentNullException(nameof(encryptionService));

            // Khởi tạo task dọn dẹp connection pool định kỳ
            _ = Task.Run(CleanupConnectionPoolAsync);
        }

        /// <summary>
        /// Kiểm tra kết nối tới server
        /// </summary>
        public async Task<bool> TestConnectionAsync(string serverAddress, int port = 61188, int timeout = 5000)
        {
            PooledTcpClient pooledClient = null;
            try
            {
                pooledClient = await GetConnectionAsync(serverAddress, port, timeout);
                if (pooledClient == null)
                {
                    return false;
                }

                string request = $"AUTH:{AUTH_TOKEN} PING";
                string response = await SendRequestAsync(pooledClient, request, timeout);

                bool success = response == "PONG";
                _logger.LogInformation("Connection test to {ServerAddress}:{Port} {Result}",
                    serverAddress, port, success ? "succeeded" : "failed");

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing connection to server {ServerAddress}:{Port}", serverAddress, port);
                return false;
            }
            finally
            {
                // Trả kết nối về pool nếu thành công, ngược lại đánh dấu như không hợp lệ
                if (pooledClient != null)
                {
                    pooledClient.UpdateLastUsed();
                }
            }
        }

        /// <summary>
        /// Lấy danh sách profile từ server
        /// </summary>
        public async Task<List<string>> GetProfileNamesAsync(string serverAddress, int port = 61188)
        {
            string cacheKey = $"{serverAddress}:{port}:profiles";

            // Kiểm tra cache trước
            if (_profileNamesCache.TryGetValue(cacheKey, out var cachedProfiles) && !cachedProfiles.IsExpired)
            {
                _logger.LogDebug("Using cached profile names for {ServerAddress}:{Port}", serverAddress, port);
                return new List<string>(cachedProfiles.Item); // Trả về bản sao để tránh race condition
            }

            PooledTcpClient pooledClient = null;
            try
            {
                pooledClient = await GetConnectionAsync(serverAddress, port);
                if (pooledClient == null)
                {
                    return new List<string>();
                }

                string request = $"AUTH:{AUTH_TOKEN} GET_PROFILES";
                string response = await SendRequestAsync(pooledClient, request);

                _logger.LogDebug("Server response for profile names: {Response}", response);

                if (response == "AUTH_FAILED")
                {
                    _logger.LogWarning("Authentication failed when connecting to server");
                    throw new Exception("Authentication failed");
                }
                else if (response == "NO_PROFILES")
                {
                    _logger.LogInformation("No profiles found on server");

                    // Cache kết quả trống
                    var emptyList = new List<string>();
                    _profileNamesCache[cacheKey] = new CachedItem<List<string>>(emptyList, TimeSpan.FromMinutes(2));

                    return emptyList;
                }
                else
                {
                    var profileNames = response.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    _logger.LogInformation("Received {Count} profiles from server", profileNames.Length);

                    // Cache kết quả
                    var result = new List<string>(profileNames);
                    _profileNamesCache[cacheKey] = new CachedItem<List<string>>(result, TimeSpan.FromMinutes(5));

                    return result;
                }
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Socket error when retrieving profile list: {Error}", ex.Message);
                // Đánh dấu kết nối là không hợp lệ
                if (pooledClient != null)
                {
                    pooledClient.Invalidate();
                    _connectionPool.TryRemove($"{serverAddress}:{port}", out _);
                }
                return new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving profile list from server: {Error}", ex.Message);
                return new List<string>();
            }
            finally
            {
                // Trả kết nối về pool thay vì đóng
                if (pooledClient != null)
                {
                    pooledClient.UpdateLastUsed();
                }
            }
        }

        /// <summary>
        /// Lấy thông tin chi tiết profile từ server theo tên
        /// </summary>
        public async Task<SteamCmdProfile> GetProfileDetailsByNameAsync(string serverAddress, string profileName, int port = 61188)
        {
            if (string.IsNullOrEmpty(profileName))
            {
                _logger.LogWarning("Profile name cannot be empty");
                return null;
            }

            string cacheKey = $"{serverAddress}:{port}:profile:{profileName}";

            // Kiểm tra cache trước
            if (_profileDetailsCache.TryGetValue(cacheKey, out var cachedProfile) && !cachedProfile.IsExpired)
            {
                _logger.LogDebug("Using cached profile details for {ProfileName}", profileName);
                return DeepClone(cachedProfile.Item); // Trả về bản sao sâu để tránh race condition
            }

            PooledTcpClient pooledClient = null;
            try
            {
                pooledClient = await GetConnectionAsync(serverAddress, port);
                if (pooledClient == null)
                {
                    return null;
                }

                string request = $"AUTH:{AUTH_TOKEN} GET_PROFILE_DETAILS {profileName}";
                string response = await SendRequestAsync(pooledClient, request);

                if (response == "AUTH_FAILED")
                {
                    _logger.LogWarning("Authentication failed when connecting to server");
                    throw new Exception("Authentication failed");
                }
                else if (response == "PROFILE_NOT_FOUND")
                {
                    _logger.LogWarning("Profile '{ProfileName}' not found on server", profileName);
                    return null;
                }
                else
                {
                    try
                    {
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var clientProfile = JsonSerializer.Deserialize<ClientProfile>(response, options);

                        if (clientProfile == null)
                            throw new Exception("Failed to parse profile data from server");

                        _logger.LogInformation("Parsed profile from server: {Name}", clientProfile.Name);

                        var profile = new SteamCmdProfile
                        {
                            Name = clientProfile.Name,
                            AppID = clientProfile.AppID,
                            InstallDirectory = clientProfile.InstallDirectory,
                            Arguments = clientProfile.Arguments ?? string.Empty,
                            ValidateFiles = clientProfile.ValidateFiles,
                            AutoRun = clientProfile.AutoRun,
                            AnonymousLogin = clientProfile.AnonymousLogin,
                            Status = "Stopped"
                        };

                        if (!clientProfile.AnonymousLogin)
                        {
                            profile.SteamUsername = clientProfile.SteamUsername;
                            profile.SteamPassword = clientProfile.SteamPassword;

                            if (!string.IsNullOrEmpty(clientProfile.SteamUsername))
                            {
                                try
                                {
                                    var decryptedUsername = _encryptionService.Decrypt(clientProfile.SteamUsername);
                                    _logger.LogDebug("Successfully decrypted username");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to decrypt username");
                                }
                            }

                            if (!string.IsNullOrEmpty(clientProfile.SteamPassword))
                            {
                                try
                                {
                                    _encryptionService.Decrypt(clientProfile.SteamPassword);
                                    _logger.LogDebug("Successfully decrypted password");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to decrypt password");
                                }
                            }
                        }
                        else
                        {
                            profile.SteamUsername = string.Empty;
                            profile.SteamPassword = string.Empty;
                        }

                        // Cache kết quả
                        _profileDetailsCache[cacheKey] = new CachedItem<SteamCmdProfile>(
                            DeepClone(profile), // Lưu bản sao để tránh sửa đổi cache từ bên ngoài
                            TimeSpan.FromMinutes(5)
                        );

                        _logger.LogInformation("Successfully retrieved details for profile '{ProfileName}' from server", profileName);
                        return profile;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error parsing profile data from server: {Error}", ex.Message);
                        throw;
                    }
                }
            }
            catch (SocketException ex)
            {
                _logger.LogError(ex, "Socket error when retrieving profile details: {Error}", ex.Message);
                // Đánh dấu kết nối là không hợp lệ
                if (pooledClient != null)
                {
                    pooledClient.Invalidate();
                    _connectionPool.TryRemove($"{serverAddress}:{port}", out _);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving profile details for '{ProfileName}': {Error}", profileName, ex.Message);
                return null;
            }
            finally
            {
                // Trả kết nối về pool thay vì đóng
                if (pooledClient != null)
                {
                    pooledClient.UpdateLastUsed();
                }
            }
        }

        /// <summary>
        /// Đồng bộ hóa profiles từ server
        /// </summary>
        public async Task<int> SyncProfilesFromServerAsync(string serverAddress, ProfileService profileService, int port = 61188)
        {
            try
            {
                var profileNames = await GetProfileNamesAsync(serverAddress, port);
                if (profileNames.Count == 0)
                    return 0;

                int syncedCount = 0;
                var currentProfiles = await profileService.GetAllProfiles();
                var tasks = new List<Task<SteamCmdProfile>>();

                // Tải các profiles song song với số lượng giới hạn
                foreach (var batch in BatchList(profileNames, 3)) // Xử lý 3 profiles cùng lúc
                {
                    var batchTasks = new List<Task<SteamCmdProfile>>();

                    foreach (var profileName in batch)
                    {
                        var task = GetProfileDetailsByNameAsync(serverAddress, profileName, port);
                        batchTasks.Add(task);
                    }

                    // Đợi tất cả các tasks trong batch hoàn thành
                    var profiles = await Task.WhenAll(batchTasks);

                    foreach (var serverProfile in profiles)
                    {
                        if (serverProfile == null)
                            continue;

                        try
                        {
                            var existingProfile = currentProfiles.FirstOrDefault(p => p.Name == serverProfile.Name);

                            if (existingProfile != null)
                            {
                                serverProfile.Id = existingProfile.Id;
                                serverProfile.Status = existingProfile.Status;
                                serverProfile.Pid = existingProfile.Pid;
                                serverProfile.StartTime = existingProfile.StartTime;
                                serverProfile.StopTime = existingProfile.StopTime;
                                serverProfile.LastRun = existingProfile.LastRun;
                                await profileService.UpdateProfile(serverProfile);
                            }
                            else
                            {
                                int newId = currentProfiles.Count > 0 ? currentProfiles.Max(p => p.Id) + 1 : 1;
                                serverProfile.Id = newId;
                                serverProfile.Status = "Stopped";
                                currentProfiles.Add(serverProfile);
                            }

                            syncedCount++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error syncing profile '{ProfileName}' from server", serverProfile.Name);
                        }
                    }
                }

                // Lưu tất cả các thay đổi vào một lần
                if (syncedCount > 0)
                {
                    await profileService.SaveProfiles(currentProfiles);
                }

                _logger.LogInformation("Synced {Count} profiles from server", syncedCount);
                return syncedCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing profiles from server");
                return 0;
            }
        }

        /// <summary>
        /// Dọn dẹp các kết nối không sử dụng và cache hết hạn định kỳ
        /// </summary>
        private async Task CleanupConnectionPoolAsync()
        {
            while (true)
            {
                try
                {
                    var now = DateTime.UtcNow;

                    // Dọn dẹp các kết nối đã không sử dụng quá 60 giây
                    var expiredConnections = _connectionPool
                        .Where(kv => kv.Value.LastUsed.AddSeconds(60) < now)
                        .ToList();

                    foreach (var conn in expiredConnections)
                    {
                        if (_connectionPool.TryRemove(conn.Key, out var client))
                        {
                            try
                            {
                                client.Dispose();
                                _logger.LogDebug("Removed expired connection to {ServerKey}", conn.Key);
                            }
                            catch { /* Ignore */ }
                        }
                    }

                    // Dọn dẹp các cache hết hạn
                    var expiredProfileNames = _profileNamesCache
                        .Where(kv => kv.Value.IsExpired)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in expiredProfileNames)
                    {
                        _profileNamesCache.TryRemove(key, out _);
                    }

                    var expiredProfileDetails = _profileDetailsCache
                        .Where(kv => kv.Value.IsExpired)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var key in expiredProfileDetails)
                    {
                        _profileDetailsCache.TryRemove(key, out _);
                    }

                    if (expiredConnections.Count > 0 || expiredProfileNames.Count > 0 || expiredProfileDetails.Count > 0)
                    {
                        _logger.LogDebug("Cleanup: removed {ConnectionCount} connections, {ProfileNameCount} profile name caches, {ProfileDetailCount} profile detail caches",
                            expiredConnections.Count, expiredProfileNames.Count, expiredProfileDetails.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during connection pool cleanup");
                }

                await Task.Delay(TimeSpan.FromMinutes(1)); // Chạy mỗi phút
            }
        }

        /// <summary>
        /// Tạo và cấu hình kết nối TCP tới server
        /// </summary>
        private async Task<PooledTcpClient> GetConnectionAsync(string serverAddress, int port = 61188, int timeout = DEFAULT_TIMEOUT)
        {
            if (string.IsNullOrEmpty(serverAddress))
            {
                _logger.LogError("Server address cannot be null or empty");
                return null;
            }

            string key = $"{serverAddress}:{port}";

            // Đợi semaphore để giới hạn số requests cho server này
            SemaphoreSlim serverSemaphore = _serverSemaphores.GetOrAdd(key, _ => new SemaphoreSlim(3, 3));
            await serverSemaphore.WaitAsync();

            try
            {
                // Đợi semaphore chung để giới hạn tổng số kết nối đồng thời
                await _connectionSemaphore.WaitAsync();

                try
                {
                    // Kiểm tra xem đã có kết nối trong pool chưa
                    if (_connectionPool.TryGetValue(key, out var pooledClient))
                    {
                        // Kiểm tra kết nối còn valid không
                        if (pooledClient.IsValid())
                        {
                            pooledClient.UpdateLastUsed();
                            _logger.LogDebug("Reusing existing connection to {ServerAddress}:{Port}", serverAddress, port);
                            return pooledClient;
                        }

                        // Kết nối không còn valid, loại bỏ
                        _logger.LogDebug("Removing invalid connection to {ServerAddress}:{Port}", serverAddress, port);
                        _connectionPool.TryRemove(key, out _);
                        pooledClient.Dispose();
                    }

                    // Tạo kết nối mới với retry logic
                    PooledTcpClient newConnection = null;
                    int retryCount = 0;
                    Exception lastException = null;

                    while (retryCount < CONNECTION_RETRY && newConnection == null)
                    {
                        try
                        {
                            var client = new TcpClient();

                            // Cấu hình TCP client để đạt hiệu suất tốt nhất
                            client.NoDelay = true; // Tắt thuật toán Nagle
                            client.ReceiveBufferSize = BUFFER_SIZE;
                            client.SendBufferSize = BUFFER_SIZE;
                            client.ReceiveTimeout = timeout;
                            client.SendTimeout = timeout;

                            var connectTask = client.ConnectAsync(serverAddress, port);
                            if (await Task.WhenAny(connectTask, Task.Delay(timeout)) != connectTask)
                            {
                                throw new TimeoutException($"Connection to {serverAddress}:{port} timed out after {timeout}ms");
                            }

                            await connectTask;

                            if (client.Connected)
                            {
                                newConnection = new PooledTcpClient(client);
                                _connectionPool[key] = newConnection;
                                _logger.LogInformation("Successfully connected to {ServerAddress}:{Port}", serverAddress, port);
                            }
                            else
                            {
                                throw new SocketException((int)SocketError.NotConnected);
                            }
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            retryCount++;
                            _logger.LogWarning("Connection attempt {RetryCount}/{MaxRetries} to {ServerAddress}:{Port} failed: {Error}",
                                retryCount, CONNECTION_RETRY, serverAddress, port, ex.Message);

                            if (retryCount < CONNECTION_RETRY)
                            {
                                // Exponential backoff
                                await Task.Delay(500 * (int)Math.Pow(2, retryCount - 1));
                            }
                        }
                    }

                    if (newConnection == null)
                    {
                        _logger.LogError("Failed to connect to {ServerAddress}:{Port} after {MaxRetries} attempts",
                            serverAddress, port, CONNECTION_RETRY);
                        return null;
                    }

                    return newConnection;
                }
                finally
                {
                    _connectionSemaphore.Release();
                }
            }
            finally
            {
                serverSemaphore.Release();
            }
        }

        /// <summary>
        /// Gửi request tới server và nhận response
        /// </summary>
        private async Task<string> SendRequestAsync(PooledTcpClient client, string request, int timeout = DEFAULT_TIMEOUT)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client), "TCP client cannot be null");
            }

            NetworkStream stream = null;
            byte[] buffer = new byte[BUFFER_SIZE];

            try
            {
                stream = client.GetStream();
                stream.ReadTimeout = timeout;
                stream.WriteTimeout = timeout;

                // Gửi thông điệp với tiền tố độ dài
                byte[] requestBytes = Encoding.UTF8.GetBytes(request);
                byte[] lengthBytes = BitConverter.GetBytes(requestBytes.Length);

                await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);
                await stream.WriteAsync(requestBytes, 0, requestBytes.Length);
                await stream.FlushAsync();

                // Nhận phản hồi với tiền tố độ dài
                int bytesRead = await ReadBytesAsync(stream, buffer, 0, 4, timeout);
                if (bytesRead < 4)
                    throw new IOException("Failed to read response length from server");

                int responseLength = BitConverter.ToInt32(buffer, 0);
                if (responseLength <= 0 || responseLength > 10 * 1024 * 1024) // Max 10MB response
                    throw new IOException($"Invalid response length received: {responseLength}");

                // Tạo buffer với kích thước phù hợp
                byte[] responseBuffer = responseLength <= buffer.Length ? buffer : new byte[responseLength];

                // Đọc đủ số byte từ server
                bytesRead = await ReadBytesAsync(stream, responseBuffer, 0, responseLength, timeout);
                if (bytesRead < responseLength)
                    throw new IOException("Connection closed before receiving full response");

                // Chuyển đổi sang chuỗi
                return Encoding.UTF8.GetString(responseBuffer, 0, bytesRead).Trim();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during communication with server");
                client.Invalidate(); // Đánh dấu kết nối không hợp lệ để tạo lại sau này
                throw;
            }
        }

        /// <summary>
        /// Đọc chính xác số byte yêu cầu từ stream
        /// </summary>
        private async Task<int> ReadBytesAsync(NetworkStream stream, byte[] buffer, int offset, int count, int timeout)
        {
            int totalBytesRead = 0;

            // Đặt CancellationTokenSource để timeout
            using (var cts = new CancellationTokenSource(timeout))
            {
                try
                {
                    while (totalBytesRead < count)
                    {
                        int bytesRead = await stream.ReadAsync(buffer, offset + totalBytesRead, count - totalBytesRead, cts.Token);
                        if (bytesRead == 0)
                        {
                            // Connection closed before all expected bytes were read
                            break;
                        }
                        totalBytesRead += bytesRead;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Timeout after {timeout}ms waiting for data");
                }
            }

            return totalBytesRead;
        }

        /// <summary>
        /// Tạo một bản sao sâu của đối tượng
        /// </summary>
        private T DeepClone<T>(T obj)
        {
            if (obj == null)
                return default;

            // Sử dụng JSON để tạo bản sao sâu
            string json = JsonSerializer.Serialize(obj);
            return JsonSerializer.Deserialize<T>(json);
        }

        /// <summary>
        /// Chia danh sách thành các batch nhỏ hơn
        /// </summary>
        private IEnumerable<List<T>> BatchList<T>(List<T> source, int batchSize)
        {
            if (source == null || source.Count == 0)
                yield break;

            for (int i = 0; i < source.Count; i += batchSize)
            {
                yield return source.GetRange(i, Math.Min(batchSize, source.Count - i));
            }
        }

        /// <summary>
        /// Xóa toàn bộ cache
        /// </summary>
        public void ClearCache()
        {
            _profileNamesCache.Clear();
            _profileDetailsCache.Clear();
            _logger.LogInformation("All caches cleared");
        }

        /// <summary>
        /// Giải phóng tài nguyên khi đối tượng bị hủy
        /// </summary>
        public void Dispose()
        {
            // Đóng tất cả các kết nối trong pool
            foreach (var client in _connectionPool.Values)
            {
                try
                {
                    client.Dispose();
                }
                catch { /* Ignore */ }
            }

            _connectionPool.Clear();

            // Giải phóng semaphores
            _connectionSemaphore?.Dispose();
            foreach (var semaphore in _serverSemaphores.Values)
            {
                try
                {
                    semaphore?.Dispose();
                }
                catch { /* Ignore */ }
            }

            _serverSemaphores.Clear();

            _logger.LogInformation("TcpClientService disposed");
        }

        /// <summary>
        /// Lớp đại diện cho một kết nối TCP được pooled
        /// </summary>
        private class PooledTcpClient : IDisposable
        {
            private TcpClient _client;
            private bool _isValid = true;
            private readonly object _lock = new object();

            public DateTime LastUsed { get; private set; }

            public PooledTcpClient(TcpClient client)
            {
                _client = client ?? throw new ArgumentNullException(nameof(client));
                LastUsed = DateTime.UtcNow;
            }

            public NetworkStream GetStream()
            {
                lock (_lock)
                {
                    if (!_isValid || _client == null)
                        throw new InvalidOperationException("TCP client is no longer valid");

                    return _client.GetStream();
                }
            }

            public bool IsValid()
            {
                lock (_lock)
                {
                    if (!_isValid || _client == null)
                        return false;

                    // Kiểm tra kết nối còn sống không
                    try
                    {
                        return _client.Connected &&
                               !(_client.Client.Poll(0, SelectMode.SelectRead) && _client.Client.Available == 0);
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            public void UpdateLastUsed()
            {
                LastUsed = DateTime.UtcNow;
            }

            public void Invalidate()
            {
                lock (_lock)
                {
                    _isValid = false;
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _isValid = false;
                    try
                    {
                        _client?.Close();
                        _client?.Dispose();
                    }
                    catch { /* Ignore */ }
                    finally
                    {
                        _client = null;
                    }
                }
            }
        }

        /// <summary>
        /// Lớp đại diện cho một mục được lưu cache có thời gian hết hạn
        /// </summary>
        private class CachedItem<T>
        {
            public T Item { get; }
            public DateTime ExpirationTime { get; }
            public bool IsExpired => DateTime.UtcNow > ExpirationTime;

            public CachedItem(T item, TimeSpan expiresIn)
            {
                Item = item;
                ExpirationTime = DateTime.UtcNow.Add(expiresIn);
            }
        }

        /// <summary>
        /// Lớp đại diện cho một profile được trả về từ server
        /// </summary>
        private class ClientProfile
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string AppID { get; set; } = string.Empty;
            public string InstallDirectory { get; set; } = string.Empty;
            public string SteamUsername { get; set; } = string.Empty;
            public string SteamPassword { get; set; } = string.Empty;
            public string Arguments { get; set; } = string.Empty;
            public bool ValidateFiles { get; set; }
            public bool AutoRun { get; set; }
            public bool AnonymousLogin { get; set; }
            public string Status { get; set; } = "Stopped";
            public DateTime StartTime { get; set; } = DateTime.Now;
            public DateTime StopTime { get; set; } = DateTime.Now;
            public int Pid { get; set; }
            public DateTime LastRun { get; set; } = DateTime.Now;
        }
    }
}