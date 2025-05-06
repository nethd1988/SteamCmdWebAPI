using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamKit2;

namespace SteamCmdWebAPI.Services
{
    public class SteamAppInfoService
    {
        private readonly ILogger<SteamAppInfoService> _logger;
        private SteamClient _steamClient;
        private SteamUser _steamUser;
        private SteamApps _steamApps;
        private CallbackManager _callbackManager;
        private bool _isConnected;
        private bool _isLoggedIn;
        private string _currentUsername;
        private string _currentPassword;
        private AutoResetEvent _connectEvent;
        private AutoResetEvent _logonEvent;
        private AutoResetEvent _appInfoEvent;
        private AutoResetEvent _licenseListEvent;
        private Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> _appInfoCache;
        private List<SteamApps.LicenseListCallback.License> _licenses;
        private HashSet<uint> _ownedAppIds;

        // Thêm biến theo dõi trạng thái 2FA
        private string _authCode;
        private string _twoFactorCode;
        private bool _isWaitingFor2FA;
        private string _requiredCodeType;
        private TaskCompletionSource<bool> _authCompletionSource;

        // Thêm biến để theo dõi trạng thái xử lý package
        private bool _isProcessingPackages;

        // Timeout và retry configs
        private readonly TimeSpan _connectionTimeout = TimeSpan.FromSeconds(10);
        private readonly TimeSpan _loginTimeout = TimeSpan.FromSeconds(15);
        private readonly TimeSpan _licenseTimeout = TimeSpan.FromSeconds(30); // Tăng timeout
        private readonly TimeSpan _appInfoTimeout = TimeSpan.FromSeconds(30); // Tăng timeout
        private readonly int _maxConnectionRetries = 3;
        private readonly int _maxLoginRetries = 2;

        public SteamAppInfoService(ILogger<SteamAppInfoService> logger)
        {
            _logger = logger;
            _appInfoCache = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            _licenses = new List<SteamApps.LicenseListCallback.License>();
            _ownedAppIds = new HashSet<uint>();

            // Khởi tạo các AutoResetEvent
            _connectEvent = new AutoResetEvent(false);
            _logonEvent = new AutoResetEvent(false);
            _appInfoEvent = new AutoResetEvent(false);
            _licenseListEvent = new AutoResetEvent(false);

            InitializeSteamClient();
        }

        private void InitializeSteamClient()
        {
            _steamClient = new SteamClient();
            _callbackManager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            _steamApps = _steamClient.GetHandler<SteamApps>();

            // Đăng ký các callback quan trọng
            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            _callbackManager.Subscribe<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo);
            _callbackManager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);

            // Bắt đầu thread callback
            StartCallbackThread();
        }

        private Task _callbackTask;
        private CancellationTokenSource _cancellationTokenSource;

        private void StartCallbackThread()
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            _callbackTask = Task.Run(() =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        // Giảm thời gian chờ giữa các lần gọi callback xuống 100ms
                        _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi trong luồng callback: {Message}", ex.Message);
                        // Đợi một chút trước khi thử lại để tránh CPU spike
                        Thread.Sleep(500);
                    }
                }
            }, token);
        }

        public async Task DisposeAsync()
        {
            // Dừng thread callback
            _cancellationTokenSource?.Cancel();
            if (_callbackTask != null)
            {
                await Task.WhenAny(_callbackTask, Task.Delay(1000));
            }

            // Ngắt kết nối khỏi Steam
            try
            {
                if (_steamClient != null && _steamClient.IsConnected)
                {
                    _steamClient.Disconnect();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi ngắt kết nối Steam trong DisposeAsync");
            }

            // Giải phóng tài nguyên
            _connectEvent?.Dispose();
            _logonEvent?.Dispose();
            _appInfoEvent?.Dispose();
            _licenseListEvent?.Dispose();
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            _logger.LogInformation("Đã kết nối đến Steam");
            _isConnected = true;
            _connectEvent.Set();

            // Nếu chưa đăng nhập nhưng có thông tin đăng nhập, thực hiện đăng nhập
            if (!_isLoggedIn && !string.IsNullOrEmpty(_currentUsername) && !string.IsNullOrEmpty(_currentPassword))
            {
                Task.Run(() => PerformLogin(_currentUsername, _currentPassword, _authCode, _twoFactorCode));
            }
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _logger.LogInformation("Đã ngắt kết nối khỏi Steam: {Reason}",
                callback.UserInitiated ? "ngắt kết nối chủ động" : "mất kết nối");
            _isConnected = false;
            _isLoggedIn = false;

            // Tự động kết nối lại nếu không phải ngắt kết nối chủ động
            if (!callback.UserInitiated)
            {
                _logger.LogInformation("Đang thử kết nối lại sau 1 giây...");
                Task.Run(async () =>
                {
                    // Giảm thời gian chờ từ 3 giây xuống 1 giây
                    await Task.Delay(1000);
                    if (!_steamClient.IsConnected && !_isConnected)
                    {
                        _steamClient.Connect();
                    }
                });
            }
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                _logger.LogInformation("Đăng nhập thành công vào Steam");
                _isLoggedIn = true;
                _isWaitingFor2FA = false;
                _authCompletionSource?.TrySetResult(true);
            }
            else if (callback.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                _logger.LogWarning("Tài khoản yêu cầu mã xác thực Steam Guard Mobile");
                _isWaitingFor2FA = true;
                _requiredCodeType = "2fa";
                _authCompletionSource?.TrySetResult(false);
            }
            else if (callback.Result == EResult.AccountLogonDenied)
            {
                _logger.LogWarning("Tài khoản yêu cầu mã xác thực qua email: {EmailDomain}", callback.EmailDomain);
                _isWaitingFor2FA = true;
                _requiredCodeType = "email";
                _authCompletionSource?.TrySetResult(false);
            }
            else
            {
                _logger.LogError("Đăng nhập thất bại: {Result}", callback.Result);
                _isLoggedIn = false;
                _authCompletionSource?.TrySetResult(false);
            }

            _logonEvent.Set();
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            _logger.LogInformation("Đã đăng xuất khỏi Steam: {Result}", callback.Result);
            _isLoggedIn = false;
        }

        private void OnLicenseList(SteamApps.LicenseListCallback callback)
        {
            try
            {
                if (callback.LicenseList != null && callback.LicenseList.Count > 0)
                {
                    _logger.LogInformation("Nhận được danh sách {0} license", callback.LicenseList.Count);
                    _licenses = callback.LicenseList.ToList();

                    // Nếu đang xử lý packages, bỏ qua để tránh trùng lặp
                    if (_isProcessingPackages)
                    {
                        _logger.LogWarning("Bỏ qua callback license list trùng lặp");
                        return;
                    }

                    _isProcessingPackages = true;

                    // Xử lý ngay các license để lấy thông tin package
                    ProcessLicenses();
                }
                else
                {
                    _logger.LogWarning("Không tìm thấy license nào");
                    _licenseListEvent.Set();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý license list: {0}", ex.Message);
                _licenseListEvent.Set();
            }
        }

        private void ProcessLicenses()
        {
            try
            {
                // Lọc các PackageID hợp lệ
                var validPackages = _licenses
                    .Where(l => l.PackageID != 0)
                    .Select(l => l.PackageID)
                    .ToList();

                _logger.LogInformation("Tìm thấy {0} package hợp lệ", validPackages.Count);

                if (validPackages.Count == 0)
                {
                    _logger.LogWarning("Không tìm thấy package hợp lệ nào");
                    _isProcessingPackages = false;
                    _licenseListEvent.Set();
                    return;
                }

                // Chia thành các batch nhỏ (tối đa 50 package mỗi lần)
                int batchSize = 50;
                foreach (var batch in BatchItems(validPackages, batchSize))
                {
                    _logger.LogInformation("Gửi yêu cầu cho batch {0} package...", batch.Count);
                    var packageRequests = batch.Select(id => new SteamApps.PICSRequest(id)).ToList();

                    // Gửi yêu cầu
                    _steamApps.PICSGetProductInfo(
                        apps: new List<SteamApps.PICSRequest>(),
                        packages: packageRequests
                    );

                    // Đợi một chút để tránh rate limit
                    Thread.Sleep(300);
                }

                // Đặt timeout để đảm bảo không bị treo
                Task.Run(() =>
                {
                    if (!_licenseListEvent.WaitOne(30000))
                    {
                        _logger.LogWarning("Hết thời gian chờ xử lý package, tiếp tục với danh sách đã có");
                        _isProcessingPackages = false;

                        // Nếu đã phát hiện được AppID, tiếp tục xử lý
                        if (_ownedAppIds.Count > 0)
                        {
                            ProcessAppIds();
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý licenses: {0}", ex.Message);
                _isProcessingPackages = false;
                _licenseListEvent.Set();
            }
        }

        private void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            try
            {
                // Xử lý thông tin package để lấy AppID
                if (callback.Packages != null && callback.Packages.Count > 0)
                {
                    _logger.LogInformation("Nhận được thông tin của {0} package", callback.Packages.Count);

                    foreach (var package in callback.Packages)
                    {
                        try
                        {
                            uint packageId = package.Key;
                            var packageData = package.Value;

                            // Thêm kiểm tra null trước khi gán vào dictionary
                            if (packageData == null || packageData.KeyValues == null)
                            {
                                continue;
                            }

                            // Tìm node "appids" - có thể nằm ở nhiều vị trí khác nhau
                            KeyValue appidsNode = packageData.KeyValues["appids"];

                            if (appidsNode != null && appidsNode.Children != null && appidsNode.Children.Count > 0)
                            {
                                // Tìm thấy node appids với cấu trúc chuẩn
                                foreach (var appNode in appidsNode.Children)
                                {
                                    // AppID có thể là Name hoặc Value của node
                                    uint appId = 0;

                                    // Thử lấy từ Name
                                    if (!string.IsNullOrEmpty(appNode.Name) && uint.TryParse(appNode.Name, out appId) && appId > 0)
                                    {
                                        _ownedAppIds.Add(appId);
                                        _logger.LogDebug("Đã tìm thấy AppID {0} từ Name node trong package {1}", appId, packageId);
                                    }
                                    // Thử lấy từ Value
                                    else if (appNode.Value != null && uint.TryParse(appNode.Value.ToString(), out appId) && appId > 0)
                                    {
                                        _ownedAppIds.Add(appId);
                                        _logger.LogDebug("Đã tìm thấy AppID {0} từ Value node trong package {1}", appId, packageId);
                                    }
                                }
                            }
                            else
                            {
                                // Thử tìm kiếm đệ quy trong cấu trúc KeyValues
                                FindAppIdsRecursively(packageData.KeyValues, packageId);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi xử lý package {PackageId}: {Message}", package.Key, ex.Message);
                        }
                    }

                    // Nếu không còn response nào nữa, chuyển sang lấy thông tin game
                    if (!callback.ResponsePending)
                    {
                        _licenseListEvent.Set();
                        _isProcessingPackages = false;

                        if (_ownedAppIds.Count > 0)
                        {
                            ProcessAppIds();
                        }
                    }
                }

                // Xử lý thông tin app để lấy tên game
                if (callback.Apps != null && callback.Apps.Count > 0)
                {
                    foreach (var app in callback.Apps)
                    {
                        try
                        {
                            uint appId = app.Key;
                            var appInfo = app.Value;

                            // Thêm kiểm tra null trước khi gán vào dictionary
                            if (appInfo != null)
                            {
                                _appInfoCache[appId] = appInfo;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Lỗi khi xử lý app {AppId}: {Message}", app.Key, ex.Message);
                        }
                    }

                    if (!callback.ResponsePending)
                    {
                        _appInfoEvent.Set();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý PICS callback: {Message}", ex.Message);

                // Đảm bảo các event được kích hoạt để không bị treo
                if (callback.Packages != null && callback.Packages.Count > 0 && !callback.ResponsePending)
                {
                    _licenseListEvent.Set();
                    _isProcessingPackages = false;
                }

                if (callback.Apps != null && callback.Apps.Count > 0 && !callback.ResponsePending)
                {
                    _appInfoEvent.Set();
                }
            }
        }

        // Hàm tìm kiếm đệ quy các AppID trong KeyValues
        private void FindAppIdsRecursively(KeyValue node, uint packageId)
        {
            if (node == null) return;

            try
            {
                // Kiểm tra tên node
                if (!string.IsNullOrEmpty(node.Name) &&
                    (node.Name.Equals("appid", StringComparison.OrdinalIgnoreCase) ||
                     node.Name.Equals("appids", StringComparison.OrdinalIgnoreCase)))
                {
                    // Nếu là node lá và có giá trị số
                    if ((node.Children == null || node.Children.Count == 0) && node.Value != null)
                    {
                        if (uint.TryParse(node.Value.ToString(), out uint appId) && appId > 0)
                        {
                            _ownedAppIds.Add(appId);
                            _logger.LogDebug("Đã tìm thấy AppID {AppId} từ node {NodeName} trong package {PackageId}",
                                appId, node.Name, packageId);
                        }
                    }
                    // Nếu có node con, duyệt qua từng node con
                    else if (node.Children != null && node.Children.Count > 0)
                    {
                        foreach (var child in node.Children)
                        {
                            if (child == null) continue;

                            // Kiểm tra Name của node con
                            if (!string.IsNullOrEmpty(child.Name) && uint.TryParse(child.Name, out uint appIdFromName) && appIdFromName > 0)
                            {
                                _ownedAppIds.Add(appIdFromName);
                                _logger.LogDebug("Đã tìm thấy AppID {0} từ Name của node con trong package {1}",
appIdFromName, packageId);
                            }
                            // Kiểm tra Value của node con
                            else if (child.Value != null && uint.TryParse(child.Value.ToString(), out uint appIdFromValue) && appIdFromValue > 0)
                            {
                                _ownedAppIds.Add(appIdFromValue);
                                _logger.LogDebug("Đã tìm thấy AppID {0} từ Value của node con trong package {1}",
                                    appIdFromValue, packageId);
                            }

                            // Đệ quy tiếp với node con
                            FindAppIdsRecursively(child, packageId);
                        }
                    }
                }
                // Trường hợp đặc biệt: nếu node có tên là số, có thể là AppID
                else if (!string.IsNullOrEmpty(node.Name) &&
                         uint.TryParse(node.Name, out uint potentialAppId) &&
                         potentialAppId > 0 &&
                         potentialAppId < 5000000) // Giới hạn số quá lớn
                {
                    // Kiểm tra nếu node này có node con tên là "gamedir" hoặc "name" - dấu hiệu là AppID
                    if (node.Children != null &&
                        (node.Children.Any(c => c != null && c.Name != null && c.Name.Equals("gamedir", StringComparison.OrdinalIgnoreCase)) ||
                         node.Children.Any(c => c != null && c.Name != null && c.Name.Equals("name", StringComparison.OrdinalIgnoreCase))))
                    {
                        _ownedAppIds.Add(potentialAppId);
                        _logger.LogDebug("Đã tìm thấy AppID {0} là tên node trong package {1}",
potentialAppId, packageId);
                    }
                }

                // Duyệt đệ quy qua tất cả node con nếu có
                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                    {
                        if (child != null)
                        {
                            FindAppIdsRecursively(child, packageId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý node {0} trong package {1}: {2}",
                    node.Name, packageId, ex.Message);
            }
        }

        private void ProcessAppIds()
        {
            try
            {
                _logger.LogInformation("Đã tìm thấy tổng cộng {0} AppID, đang lấy thông tin chi tiết...", _ownedAppIds.Count);

                if (_ownedAppIds.Count == 0)
                {
                    _logger.LogWarning("Không có AppID nào để xử lý");
                    _appInfoEvent.Set();
                    return;
                }

                // Lọc bỏ các AppID không hợp lệ (từ 0 đến 9 là component của Steam)
                _ownedAppIds.RemoveWhere(id => id >= 0 && id <= 9);

                // Chia thành các batch nhỏ (tối đa 100 app mỗi lần)
                int batchSize = 100;
                foreach (var batch in BatchItems(_ownedAppIds.ToList(), batchSize))
                {
                    _logger.LogInformation("Gửi yêu cầu cho batch {0} ứng dụng...", batch.Count);
                    var appRequests = batch.Select(id => new SteamApps.PICSRequest(id)).ToList();

                    // Gửi yêu cầu
                    _steamApps.PICSGetProductInfo(
                        apps: appRequests,
                        packages: new List<SteamApps.PICSRequest>()
                    );

                    // Đợi một chút để tránh rate limit
                    Thread.Sleep(300);
                }

                // Đặt timeout để đảm bảo không bị treo
                Task.Run(() => {
                    if (!_appInfoEvent.WaitOne(30000))
                    {
                        _logger.LogWarning("Hết thời gian chờ xử lý app, tiếp tục với danh sách đã có");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi xử lý AppIDs: {0}", ex.Message);
                _appInfoEvent.Set();
            }
        }

        private async Task<bool> ConnectAsync()
        {
            if (_isConnected)
            {
                return true;
            }

            try
            {
                // Thêm retry logic cho kết nối
                for (int retryCount = 0; retryCount <= _maxConnectionRetries; retryCount++)
                {
                    if (retryCount > 0)
                    {
                        _logger.LogWarning("Thử kết nối lại lần {RetryCount}/{MaxRetries}...",
                            retryCount, _maxConnectionRetries);
                        // Tăng thời gian chờ theo số lần retry
                        await Task.Delay(500 * retryCount);
                    }

                    _logger.LogInformation("Đang kết nối đến Steam...");
                    _connectEvent.Reset();
                    _steamClient.Connect();

                    bool connected = _connectEvent.WaitOne((int)_connectionTimeout.TotalMilliseconds);

                    if (connected)
                    {
                        return true;
                    }

                    // Ngắt kết nối trước khi thử lại
                    if (_steamClient.IsConnected)
                    {
                        _steamClient.Disconnect();
                    }
                }

                _logger.LogError("Kết nối đến Steam bị timeout sau {RetryCount} lần thử", _maxConnectionRetries + 1);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi kết nối đến Steam: {Message}", ex.Message);
                return false;
            }
        }

        private bool PerformLogin(string username, string password, string authCode = null, string twoFactorCode = null)
        {
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                _logger.LogError("Tên đăng nhập hoặc mật khẩu không được để trống");
                return false;
            }

            try
            {
                // Thêm retry logic cho đăng nhập
                for (int retryCount = 0; retryCount <= _maxLoginRetries; retryCount++)
                {
                    if (retryCount > 0)
                    {
                        _logger.LogWarning("Thử đăng nhập lại lần {RetryCount}/{MaxRetries}...",
                            retryCount, _maxLoginRetries);
                        // Tăng thời gian chờ theo số lần retry
                        Thread.Sleep(500 * retryCount);
                    }

                    _currentUsername = username;
                    _currentPassword = password;
                    _authCode = authCode;
                    _twoFactorCode = twoFactorCode;

                    var loginDetails = new SteamUser.LogOnDetails
                    {
                        Username = username,
                        Password = password,
                        ShouldRememberPassword = true // Thêm flag này để giảm số lần đăng nhập trong tương lai
                    };

                    if (!string.IsNullOrEmpty(authCode))
                    {
                        loginDetails.AuthCode = authCode;
                    }

                    if (!string.IsNullOrEmpty(twoFactorCode))
                    {
                        loginDetails.TwoFactorCode = twoFactorCode;
                    }

                    _logger.LogInformation("Đang đăng nhập vào Steam với tài khoản {Username}...", username);
                    _logonEvent.Reset();
                    _steamUser.LogOn(loginDetails);

                    bool loggedOn = _logonEvent.WaitOne((int)_loginTimeout.TotalMilliseconds);

                    if (loggedOn && _isLoggedIn)
                    {
                        return true;
                    }

                    // Nếu đang chờ 2FA, không retry nữa
                    if (_isWaitingFor2FA)
                    {
                        return false;
                    }
                }

                _logger.LogError("Đăng nhập vào Steam bị timeout hoặc thất bại sau {RetryCount} lần thử", _maxLoginRetries + 1);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đăng nhập vào Steam: {Message}", ex.Message);
                return false;
            }
        }

        public async Task<List<(string AppId, string GameName)>> GetOwnedGamesAsync(string username, string password)
        {
            _logger.LogInformation("Đang lấy danh sách game cho tài khoản {Username}...", username);

            // Reset các biến trạng thái
            _ownedAppIds.Clear();
            _licenses.Clear();
            _isProcessingPackages = false;
            _authCompletionSource = new TaskCompletionSource<bool>();

            // Kết nối đến Steam
            bool connected = await ConnectAsync();
            if (!connected)
            {
                _logger.LogError("Không thể kết nối đến Steam");
                return new List<(string, string)>();
            }

            // Đăng nhập
            bool loggedIn = PerformLogin(username, password);
            if (!loggedIn)
            {
                // Nếu cần xác thực 2FA
                if (_isWaitingFor2FA)
                {
                    _logger.LogWarning("Tài khoản yêu cầu xác thực {Type}", _requiredCodeType == "2fa" ? "Steam Guard" : "email");
                    throw new Exception($"Cần mã xác thực từ {(_requiredCodeType == "2fa" ? "ứng dụng Steam Guard" : "email")}");
                }

                _logger.LogError("Không thể đăng nhập vào Steam");
                return new List<(string, string)>();
            }

            // Reset các event
            _licenseListEvent.Reset();
            _appInfoEvent.Reset();

            // Yêu cầu danh sách license
            _logger.LogInformation("Đăng nhập thành công, đang lấy danh sách license...");
            var licenseMsg = new ClientMsgProtobuf<SteamKit2.Internal.CMsgClientLicenseList>(EMsg.ClientLicenseList);
            _steamClient.Send(licenseMsg);

            // Đợi xử lý license với timeout
            bool licensesProcessed = _licenseListEvent.WaitOne((int)_licenseTimeout.TotalMilliseconds);
            if (!licensesProcessed)
            {
                _logger.LogWarning("Timeout khi lấy và xử lý licenses, thử gửi lại yêu cầu...");
                // Thử gửi lại yêu cầu
                _licenseListEvent.Reset();
                _steamClient.Send(licenseMsg);
                licensesProcessed = _licenseListEvent.WaitOne((int)_licenseTimeout.TotalMilliseconds);

                if (!licensesProcessed)
                {
                    _logger.LogWarning("Vẫn timeout khi xử lý licenses, tiếp tục với AppID đã thu thập được");
                }
            }

            // Đợi xử lý thông tin app với timeout
            bool appsProcessed = _appInfoEvent.WaitOne((int)_appInfoTimeout.TotalMilliseconds);
            if (!appsProcessed)
            {
                _logger.LogWarning("Timeout khi lấy thông tin app");
            }

            // Tạo kết quả
            var results = new List<(string AppId, string GameName)>();

            foreach (var appId in _ownedAppIds)
            {
                // Bỏ qua các appId từ 0 đến 9 vì là component của Steam
                if (appId >= 0 && appId <= 9)
                {
                    continue;
                }

                // Cố gắng lấy tên game từ cache
                string name = "Unknown";

                if (_appInfoCache.TryGetValue(appId, out var appInfo) &&
                    appInfo.KeyValues != null)
                {
                    var common = appInfo.KeyValues["common"];
                    if (common != null)
                    {
                        var nameNode = common["name"];
                        if (nameNode != null && nameNode.Value != null)
                        {
                            name = nameNode.Value.ToString();
                        }
                    }
                }

                if (name == "Unknown")
                {
                    name = $"Game {appId}";
                }

                // Kiểm tra nếu tên game có định dạng "Game X" thì bỏ qua
                if (name.StartsWith("Game ") && int.TryParse(name.Substring(5), out _))
                {
                    continue;
                }

                results.Add((appId.ToString(), name));
            }

            _logger.LogInformation("Đã lấy được {0} game từ tài khoản Steam", results.Count);

            // Ngắt kết nối để giải phóng tài nguyên
            if (_steamClient.IsConnected)
            {
                _steamClient.Disconnect();
            }

            return results;
        }

        public async Task<List<(string AppId, string GameName)>> GetAppInfoBatchAsync(IEnumerable<string> appIds)
        {
            var results = new List<(string AppId, string GameName)>();

            try
            {
                // Tạo danh sách các AppID hợp lệ
                var validAppIds = appIds
                    .Where(id => !string.IsNullOrEmpty(id) && uint.TryParse(id, out uint numId) && numId > 10 && numId < 5000000)
                    .ToList();

                if (validAppIds.Count == 0)
                {
                    _logger.LogWarning("Không có AppID hợp lệ nào trong danh sách đầu vào");
                    return results;
                }

                _logger.LogInformation("Đang lấy thông tin cho {0} ứng dụng", validAppIds.Count);

                foreach (var appId in validAppIds)
                {
                    if (uint.TryParse(appId, out uint id) && id > 0)
                    {
                        // Bỏ qua các appId từ 0 đến 9 (system apps)
                        if (id >= 0 && id <= 9)
                        {
                            continue;
                        }

                        // Tìm trong cache
                        string name = "";
                        if (_appInfoCache.TryGetValue(id, out var appInfo) && appInfo?.KeyValues != null)
                        {
                            var common = appInfo.KeyValues["common"];
                            if (common != null)
                            {
                                var nameNode = common["name"];
                                if (nameNode != null && nameNode.Value != null)
                                {
                                    name = nameNode.Value.ToString();
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(name))
                        {
                            name = $"Game {id}";
                        }

                        // Kiểm tra nếu tên game có định dạng "Game X" thì bỏ qua
                        if (name.StartsWith("Game ") && int.TryParse(name.Substring(5), out _))
                        {
                            continue;
                        }
                        
                        // Kiểm tra tên hợp lệ
                        if (name.Length < 2 || name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        results.Add((id.ToString(), name));
                    }
                }

                _logger.LogInformation("Đã lấy thông tin cho {0} ứng dụng thành công", results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy thông tin AppID: {0}", ex.Message);
            }

            return results;
        }

        public async Task<List<(string AppId, string GameName)>> ScanAccountGames(string username, string password)
        {
            // Đơn giản hóa - gọi thẳng đến hàm xử lý chính
            try
            {
                _logger.LogInformation("Đang quét danh sách game cho tài khoản {0}...", username);
                return await GetOwnedGamesAsync(username, password);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi quét game từ tài khoản: {0}", ex.Message);
                throw;
            }
        }

        // Hàm chia danh sách thành các batch nhỏ hơn
        private IEnumerable<List<T>> BatchItems<T>(List<T> source, int batchSize)
        {
            for (int i = 0; i < source.Count; i += batchSize)
            {
                yield return source.Skip(i).Take(batchSize).ToList();
            }
        }
    }
}
