using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SteamKit2;
using System.Text.RegularExpressions;

namespace SteamCmdWebAPI.Services
{
    public class SteamAppInfoService
    {
        private readonly ILogger<SteamAppInfoService> _logger;
        private readonly EncryptionService _encryptionService;
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
        private HashSet<uint> _familySharedAppIds;
        private HashSet<uint> _hiddenAppIds;

        // Thêm biến theo dõi trạng thái 2FA
        private string _authCode;
        private string _twoFactorCode;
        private bool _isWaitingFor2FA;
        private string _requiredCodeType;
        private TaskCompletionSource<bool> _authCompletionSource;

        // Thêm biến để theo dõi trạng thái xử lý package
        private bool _isProcessingPackages;

        // Timeout và retry configs - Tăng timeout để đảm bảo xử lý đủ game
        private readonly TimeSpan _connectionTimeout = TimeSpan.FromSeconds(15);
        private readonly TimeSpan _loginTimeout = TimeSpan.FromSeconds(20);
        private readonly TimeSpan _licenseTimeout = TimeSpan.FromMinutes(2); // Tăng từ 30 giây lên 2 phút
        private readonly TimeSpan _appInfoTimeout = TimeSpan.FromMinutes(2); // Tăng từ 30 giây lên 2 phút
        private readonly int _maxConnectionRetries = 3;
        private readonly int _maxLoginRetries = 2;

        // Thêm các regex patterns để phát hiện game ẩn
        private static readonly Regex[] HiddenGamePatterns = new[]
        {
            new Regex(@"hidden=1", RegexOptions.Compiled),
            new Regex(@"visible=0", RegexOptions.Compiled),
            new Regex(@"visibleinlibrary=0", RegexOptions.Compiled)
        };

        // Tạo PICSRequest cho SteamKit 3.1.0
        public class PICSRequestWrapper
        {
            public uint ID { get; set; }
            public ulong AccessToken { get; set; }
            public bool Public { get; set; }

            public PICSRequestWrapper(uint id, ulong accessToken = 0, bool isPublic = true)
            {
                ID = id;
                AccessToken = accessToken;
                Public = isPublic;
            }

            public SteamApps.PICSRequest ToPICSRequest()
            {
                var request = new SteamApps.PICSRequest
                {
                    ID = ID,
                    AccessToken = AccessToken
                };
                return request;
            }
        }

        public SteamAppInfoService(ILogger<SteamAppInfoService> logger, EncryptionService encryptionService)
        {
            _logger = logger;
            _encryptionService = encryptionService;
            _appInfoCache = new Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo>();
            _licenses = new List<SteamApps.LicenseListCallback.License>();
            _ownedAppIds = new HashSet<uint>();
            _familySharedAppIds = new HashSet<uint>();
            _hiddenAppIds = new HashSet<uint>();

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
                    // Ensure we log off properly before disconnecting
                    if (_isLoggedIn && _steamUser != null)
                    {
                        _logger.LogInformation("Logging off before disconnection in DisposeAsync");
                        _steamUser.LogOff();
                        _isLoggedIn = false;
                        // Brief delay to allow Steam servers to process the logoff
                        await Task.Delay(500);
                    }

                    _logger.LogInformation("Ngắt kết nối Steam trong DisposeAsync");
                    _steamClient.Disconnect();
                    _isConnected = false;
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
                // Giải mã thông tin đăng nhập trước khi sử dụng
                string decryptedUsername;
                string decryptedPassword;

                try
                {
                    decryptedUsername = _encryptionService.Decrypt(_currentUsername);
                    _logger.LogDebug("OnConnected: Đã giải mã username thành công");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnConnected: Không thể giải mã username - có thể đã không được mã hóa");
                    // Nếu không giải mã được, giả sử đó là văn bản thuần
                    decryptedUsername = _currentUsername;
                }

                try
                {
                    decryptedPassword = _encryptionService.Decrypt(_currentPassword);
                    _logger.LogDebug("OnConnected: Đã giải mã password thành công");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OnConnected: Không thể giải mã password - có thể đã không được mã hóa");
                    // Nếu không giải mã được, giả sử đó là văn bản thuần
                    decryptedPassword = _currentPassword;
                }

                // Kiểm tra tính hợp lệ trước khi thử đăng nhập
                if (string.IsNullOrEmpty(decryptedUsername) || string.IsNullOrEmpty(decryptedPassword))
                {
                    _logger.LogError("OnConnected: Username hoặc password sau khi giải mã là rỗng, không thể đăng nhập");
                    return;
                }

                _logger.LogInformation("OnConnected: Đang tự động đăng nhập với username: {UsernameHint}***",
                    decryptedUsername.Length > 3 ? decryptedUsername.Substring(0, 3) : "***");

                Task.Run(() => PerformLogin(decryptedUsername, decryptedPassword, _authCode, _twoFactorCode));
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

                    // Tìm kiếm license Family Sharing và Hidden Games
                    foreach (var license in _licenses)
                    {
                        // Phân tích các license để tìm các game được chia sẻ từ Family Sharing
                        // Dựa vào các thuộc tính đặc trưng thay vì dùng flag SharedLicense
                        if ((int)license.PaymentMethod == 10 || (int)license.PaymentMethod == 0 || (int)license.PaymentMethod == 11)
                        {
                            // Kiểm tra thêm các điều kiện khác để nhận diện Family Sharing
                            if ((int)license.LicenseType == 1 || (int)license.LicenseType == 0 || (int)license.LicenseType == 4)
                            {
                                _logger.LogDebug("Phát hiện license có thể từ Family Sharing: PackageID={0}, LicenseType={1}, PaymentMethod={2}",
                                    license.PackageID, (int)license.LicenseType, (int)license.PaymentMethod);

                                // Thực hiện truy vấn PICS cho package này
                                // Tạo PICSRequest cho SteamKit 3.1.0
                                var packageRequest = new SteamApps.PICSRequest();
                                packageRequest.ID = license.PackageID;
                                var packages = new List<SteamApps.PICSRequest> { packageRequest };
                                _steamApps.PICSGetProductInfo(
                                    apps: new List<SteamApps.PICSRequest>(),
                                    packages: packages
                                );

                                // Đánh dấu package này là từ Family Sharing (để xử lý trong OnPICSProductInfo)
                                _familySharedAppIds.Add(license.PackageID);
                            }
                        }

                        // Tìm kiếm các game ẩn
                        try
                        {
                            // Kiểm tra flag license.Flags nếu có
                            bool isHidden = false;
                            var licenseType = license.GetType();
                            var flagsProperty = licenseType.GetProperty("Flags");

                            if (flagsProperty != null)
                            {
                                var flags = flagsProperty.GetValue(license);
                                // Kiểm tra bit thứ 5 (32) - thường được dùng cho hidden flags
                                if (flags != null && ((uint)flags & 32u) != 0)
                                {
                                    isHidden = true;
                                    _logger.LogDebug("Phát hiện license ẩn từ Flags: PackageID={0}", license.PackageID);
                                }
                            }

                            // Nếu không tìm thấy từ Flags, bỏ qua vì sẽ được xử lý trong ProcessLicenses
                            if (!isHidden)
                            {
                                // Với SteamKit 3.1.0, không còn cần truy vấn đặc biệt onlyPublic
                                // Package sẽ được xử lý trong ProcessLicenses
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("Lỗi khi tìm kiếm game ẩn: {0}", ex.Message);
                        }
                    }

                    _logger.LogInformation("Đã xử lý license list và yêu cầu thông tin game");
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
                // Thêm biến theo dõi thời gian xử lý
                var processingStartTime = DateTime.Now;
                var maxProcessingTime = TimeSpan.FromMinutes(3); // Tối đa 3 phút

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

                // Thực hiện phân tích trực tiếp license trước khi gửi yêu cầu PICS
                // Đây là bước bổ sung để bảo đảm có nhiều cơ hội tìm ra AppID
                ExtractAppIdsFromLicenses();

                // Giới hạn số package xử lý để tránh timeout
                if (validPackages.Count > 200)
                {
                    _logger.LogWarning("Quá nhiều package ({0}), giới hạn xuống 200 để tránh timeout", validPackages.Count);
                    validPackages = validPackages.Take(200).ToList();
                }

                // Cache để theo dõi các package đã xử lý - có thể truy cập từ các hàm khác
                var processedPackages = new HashSet<uint>();

                // Chia thành các batch nhỏ (tối đa 25 package mỗi lần thay vì 50)
                int batchSize = 25;

                // Sử dụng vòng lặp for thay vì foreach với BatchItems
                for (int i = 0; i < validPackages.Count; i += batchSize)
                {
                    // Kiểm tra nếu đã xử lý quá lâu
                    if (DateTime.Now - processingStartTime > maxProcessingTime)
                    {
                        _logger.LogWarning("Đã vượt quá thời gian xử lý package tối đa ({0} phút), dừng và tiếp tục với dữ liệu đã có",
                            maxProcessingTime.TotalMinutes);
                        break;
                    }

                    var batch = validPackages.Skip(i).Take(batchSize).ToList();

                    // Bỏ qua các package đã xử lý
                    var unprocessedBatch = new List<uint>();
                    foreach (var packageId in batch)
                    {
                        if (!processedPackages.Contains(packageId))
                        {
                            unprocessedBatch.Add(packageId);
                        }
                    }

                    if (unprocessedBatch.Count == 0)
                    {
                        _logger.LogDebug("Bỏ qua batch toàn package đã xử lý");
                        continue;
                    }

                    _logger.LogInformation("Gửi yêu cầu cho batch {0}-{1}/{2} package...",
                        i + 1, Math.Min(i + batchSize, validPackages.Count), validPackages.Count);

                    try
                    {
                        // Tạo danh sách PICSRequest cho SteamKit 3.1.0
                        var packageRequests = new List<SteamApps.PICSRequest>();
                        foreach (var id in unprocessedBatch)
                        {
                            packageRequests.Add(new SteamApps.PICSRequest(id));
                        }

                        // Gửi yêu cầu
                        _steamApps.PICSGetProductInfo(
                            apps: new List<SteamApps.PICSRequest>(),
                            packages: packageRequests
                        );

                        // Đánh dấu những package này là đã xử lý
                        foreach (var id in unprocessedBatch)
                        {
                            processedPackages.Add(id);
                        }

                        // Đợi một chút để tránh rate limit - tăng từ 500ms lên 600ms
                        Thread.Sleep(600);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý batch package {0}-{1}: {2}",
                            i + 1, Math.Min(i + batchSize, validPackages.Count), ex.Message);
                        // Đợi thêm thời gian nếu gặp lỗi
                        Thread.Sleep(1000);
                    }
                }

                // Giảm timeout xuống 60 giây thay vì 2 phút
                var reducedTimeout = TimeSpan.FromSeconds(60);

                // Đặt timeout để đảm bảo không bị treo - sử dụng reducedTimeout thay vì _licenseTimeout
                Task.Run(() =>
                {
                    if (!_licenseListEvent.WaitOne((int)reducedTimeout.TotalMilliseconds))
                    {
                        _logger.LogWarning("Hết thời gian chờ xử lý package ({0} giây), tiếp tục với danh sách đã có",
                            reducedTimeout.TotalSeconds);
                        _isProcessingPackages = false;

                        // Nếu đã phát hiện được AppID, tiếp tục xử lý
                        if (_ownedAppIds.Count > 0)
                        {
                            _logger.LogInformation("Đã tìm thấy {0} AppID, tiếp tục xử lý", _ownedAppIds.Count);
                            ProcessAppIds();
                        }
                        else
                        {
                            // Đảm bảo event được set trong trường hợp không tìm thấy AppID
                            _appInfoEvent.Set();
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

        // Phương thức mới để trích xuất AppID trực tiếp từ đối tượng License
        private void ExtractAppIdsFromLicenses()
        {
            if (_licenses == null || _licenses.Count == 0)
            {
                return;
            }

            _logger.LogInformation("Phân tích trực tiếp {0} đối tượng License...", _licenses.Count);
            int extractedAppIds = 0;

            foreach (var license in _licenses)
            {
                try
                {
                    // Kiểm tra các trường liên quan đến AppID trong License
                    if (license.AccessToken > 0 && license.AccessToken < 5000000)
                    {
                        _ownedAppIds.Add((uint)license.AccessToken);
                        extractedAppIds++;
                    }

                    // Một số license chứa AppID trong PackageID hoặc có quan hệ 1:1 với AppID
                    if (license.PackageID > 0 && license.PackageID < 5000000)
                    {
                        // Kiểm tra PaymentMethod thay vì sử dụng HasFlag với enum cụ thể
                        bool isSpecialLicense = false;

                        // Số đại diện cho FreeOnDemand thường là 10
                        if ((int)license.PaymentMethod == 10)
                        {
                            isSpecialLicense = true;
                        }

                        // Số đại diện cho HardwarePromo thường là 13
                        if ((int)license.PaymentMethod == 13)
                        {
                            isSpecialLicense = true;
                        }

                        if (!isSpecialLicense)
                        {
                            // Thử tìm AppID tương ứng với PackageID này
                            uint potentialAppId = license.PackageID;

                            // Một số package có AppID = PackageID, hoặc AppID = PackageID - offset
                            for (int offset = 0; offset <= 10; offset++)
                            {
                                if (potentialAppId > offset)
                                {
                                    uint appIdCandidate = potentialAppId - (uint)offset;
                                    if (appIdCandidate > 0 && appIdCandidate < 5000000)
                                    {
                                        _ownedAppIds.Add(appIdCandidate);
                                        extractedAppIds++;

                                        // Nếu tìm được, không cần thử offset khác
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    // Kiểm tra các trường khác của license (cho các phiên bản SteamKit mới hơn)
                    // Các trường này có thể thay đổi theo phiên bản của SteamKit
                    var licenseType = license.GetType();
                    var properties = licenseType.GetProperties();

                    foreach (var prop in properties)
                    {
                        try
                        {
                            var value = prop.GetValue(license);
                            if (value != null)
                            {
                                // Nếu có thuộc tính là Collection hoặc Array
                                if (value is System.Collections.IEnumerable collection && !(value is string))
                                {
                                    foreach (var item in collection)
                                    {
                                        if (item != null && uint.TryParse(item.ToString(), out uint appId) &&
                                            appId > 0 && appId < 5000000)
                                        {
                                            _ownedAppIds.Add(appId);
                                            extractedAppIds++;
                                        }
                                    }
                                }
                                // Nếu có thuộc tính AppID
                                else if ((prop.Name.Contains("AppID") || prop.Name.Contains("AppId")) &&
                                      uint.TryParse(value.ToString(), out uint appId) &&
                                      appId > 0 && appId < 5000000)
                                {
                                    _ownedAppIds.Add(appId);
                                    extractedAppIds++;
                                }
                            }
                        }
                        catch
                        {
                            // Bỏ qua lỗi khi truy cập thuộc tính
                            continue;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Lỗi khi phân tích đối tượng License: {0}", ex.Message);
                }
            }

            _logger.LogInformation("Đã trích xuất {0} AppID trực tiếp từ đối tượng License", extractedAppIds);
        }

        private void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback)
        {
            // Đảm bảo rằng giữa các lần callback đủ thời gian để xử lý
            try
            {
                if (callback.Packages != null && callback.Packages.Count > 0)
                {
                    _logger.LogInformation("Nhận được thông tin của {0} package, tổng số AppId trong cache: {1}",
                        callback.Packages.Count, _ownedAppIds.Count + _familySharedAppIds.Count + _hiddenAppIds.Count);

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

                            // Kiểm tra xem package này là từ Family Sharing hay không
                            bool isFromFamilySharing = IsPackageFromFamilySharing(packageId);
                            HashSet<uint> targetAppIdSet = isFromFamilySharing ? _familySharedAppIds : _ownedAppIds;

                            // Kiểm tra thuộc tính hidden của package
                            bool isPackageHidden = false;
                            try
                            {
                                var billingTypeNode = packageData.KeyValues?["billingtype"];
                                if (billingTypeNode != null && billingTypeNode.Value != null)
                                {
                                    int billingType = 0;
                                    if (int.TryParse(billingTypeNode.Value.ToString(), out billingType))
                                    {
                                        // Billing type 11 thường là cho các hidden games
                                        if (billingType == 11 || billingType == 0 || billingType == 10)
                                        {
                                            var statusNode = packageData.KeyValues?["status"];
                                            if (statusNode != null && statusNode.Value != null)
                                            {
                                                var status = statusNode.Value.ToString().ToLowerInvariant();
                                                if (status.Contains("hidden") || status == "0")
                                                {
                                                    isPackageHidden = true;
                                                    _logger.LogDebug("Phát hiện package ẩn: {0}", packageId);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug("Lỗi khi kiểm tra thuộc tính hidden của package {0}: {1}", packageId, ex.Message);
                            }

                            // Tìm node "appids" - có thể nằm ở nhiều vị trí khác nhau
                            KeyValue appidsNode = packageData.KeyValues?["appids"];

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
                                        targetAppIdSet.Add(appId);

                                        // Nếu package này là package ẩn, thêm appId vào _hiddenAppIds
                                        if (isPackageHidden)
                                        {
                                            _hiddenAppIds.Add(appId);
                                            _logger.LogDebug("Đã tìm thấy Hidden AppID {0} từ Name node trong package {1}", appId, packageId);
                                        }
                                        else
                                        {
                                            _logger.LogDebug("Đã tìm thấy AppID {0} từ Name node trong package {1}{2}",
                                                appId, packageId, isFromFamilySharing ? " (Family Sharing)" : "");
                                        }
                                    }
                                    // Thử lấy từ Value
                                    else if (appNode.Value != null && uint.TryParse(appNode.Value.ToString(), out appId) && appId > 0)
                                    {
                                        targetAppIdSet.Add(appId);
                                        _logger.LogDebug("Đã tìm thấy AppID {0} từ Value node trong package {1}{2}",
                                            appId, packageId, isFromFamilySharing ? " (Family Sharing)" : "");
                                    }
                                }
                            }
                            else
                            {
                                // Thử tìm kiếm đệ quy trong cấu trúc KeyValues
                                FindAppIdsRecursively(packageData.KeyValues, packageId, isFromFamilySharing);
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
                        _logger.LogInformation("Đã nhận đủ dữ liệu từ Steam");
                        _licenseListEvent.Set();
                        _isProcessingPackages = false;

                        if (_ownedAppIds.Count > 0)
                        {
                            ProcessAppIds();
                        }
                    }
                    else
                    {
                        _logger.LogInformation("Đang đợi thêm dữ liệu package từ Steam...");
                    }
                }

                // Xử lý thông tin app để lấy tên game
                if (callback.Apps != null && callback.Apps.Count > 0)
                {
                    _logger.LogInformation("Nhận được thông tin của {0} app, tổng số apps trong cache hiện tại: {1}",
                        callback.Apps.Count, _appInfoCache.Count);
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
                        _logger.LogInformation("Đã nhận đủ thông tin app từ Steam");
                        _appInfoEvent.Set();
                    }
                    else
                    {
                        _logger.LogInformation("Đang đợi thêm dữ liệu app từ Steam...");
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
        private void FindAppIdsRecursively(KeyValue node, uint packageId, bool isFromFamilySharing = false)
        {
            if (node == null) return;

            try
            {
                // Xác định set mục tiêu tùy thuộc vào nguồn
                HashSet<uint> targetAppIdSet = isFromFamilySharing ? _familySharedAppIds : _ownedAppIds;
                string sourceTag = isFromFamilySharing ? " (Family Sharing)" : "";

                // Kiểm tra xem có phải là Series không, nếu là Series thì đánh dấu vào _hiddenAppIds
                bool isSeries = false;

                // Kiểm tra node type
                if (node.Name != null && node.Name.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                    node.Value != null && node.Value.ToString().Equals("Series", StringComparison.OrdinalIgnoreCase))
                {
                    isSeries = true;
                    _logger.LogDebug("Phát hiện Series từ node type trong package {0}", packageId);
                }

                // Kiểm tra các node metadata liên quan đến Series
                if (node.Name != null &&
                    (node.Name.Equals("series", StringComparison.OrdinalIgnoreCase) ||
                     node.Name.Equals("episodes", StringComparison.OrdinalIgnoreCase) ||
                     node.Name.Equals("seasons", StringComparison.OrdinalIgnoreCase)))
                {
                    isSeries = true;
                    _logger.LogDebug("Phát hiện Series từ node metadata trong package {0}: node={1}", packageId, node.Name);
                }

                // Nếu là Series, đánh dấu để bỏ qua sau này
                if (isSeries && uint.TryParse(node.Name, out uint seriesAppId) && seriesAppId > 0)
                {
                    _hiddenAppIds.Add(seriesAppId);
                    _logger.LogDebug("Đã đánh dấu AppID {0} là Series để bỏ qua", seriesAppId);
                }

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
                            targetAppIdSet.Add(appId);
                            _logger.LogDebug("Đã tìm thấy AppID {AppId} từ node {NodeName} trong package {PackageId}{Source}",
                                appId, node.Name, packageId, sourceTag);
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
                                targetAppIdSet.Add(appIdFromName);
                                _logger.LogDebug("Đã tìm thấy AppID {0} từ Name của node con trong package {1}{2}",
                                    appIdFromName, packageId, sourceTag);
                            }
                            // Kiểm tra Value của node con
                            else if (child.Value != null && uint.TryParse(child.Value.ToString(), out uint appIdFromValue) && appIdFromValue > 0)
                            {
                                targetAppIdSet.Add(appIdFromValue);
                                _logger.LogDebug("Đã tìm thấy AppID {0} từ Value của node con trong package {1}{2}",
                                    appIdFromValue, packageId, sourceTag);
                            }

                            // Đệ quy tiếp với node con
                            FindAppIdsRecursively(child, packageId, isFromFamilySharing);
                        }
                    }
                }
                // Trường hợp đặc biệt: nếu node có tên là số, có thể là AppID
                else if (!string.IsNullOrEmpty(node.Name) &&
                         uint.TryParse(node.Name, out uint potentialAppId) &&
                         potentialAppId > 0 &&
                         potentialAppId < 5000000) // Giới hạn số quá lớn
                {
                    // Kiểm tra nếu node này có node con tên là các thuộc tính liên quan đến game
                    bool isAppIdNode = false;
                    bool isSeriesNode = false;

                    if (node.Children != null)
                    {
                        // Kiểm tra xem đây có phải là node Series không
                        foreach (var child in node.Children)
                        {
                            if (child != null && !string.IsNullOrEmpty(child.Name))
                            {
                                // Kiểm tra các thuộc tính đặc trưng của Series
                                if (child.Name.Equals("type", StringComparison.OrdinalIgnoreCase) &&
                                    child.Value != null && child.Value.ToString().Equals("Series", StringComparison.OrdinalIgnoreCase))
                                {
                                    isSeriesNode = true;
                                    _logger.LogDebug("Phát hiện AppID {0} là Series từ node type", potentialAppId);
                                    break;
                                }

                                // Kiểm tra các thuộc tính metadata liên quan đến Series
                                if (child.Name.Equals("series", StringComparison.OrdinalIgnoreCase) ||
                                    child.Name.Equals("episodes", StringComparison.OrdinalIgnoreCase) ||
                                    child.Name.Equals("seasons", StringComparison.OrdinalIgnoreCase))
                                {
                                    isSeriesNode = true;
                                    _logger.LogDebug("Phát hiện AppID {0} là Series từ metadata: {1}", potentialAppId, child.Name);
                                    break;
                                }
                            }
                        }

                        isAppIdNode = node.Children.Any(c => c != null && c.Name != null &&
                            (c.Name.Equals("gamedir", StringComparison.OrdinalIgnoreCase) ||
                             c.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                             c.Name.Equals("install_dir", StringComparison.OrdinalIgnoreCase) ||
                             c.Name.Equals("executable", StringComparison.OrdinalIgnoreCase) ||
                             c.Name.Equals("clienticon", StringComparison.OrdinalIgnoreCase) ||
                             c.Name.Equals("clienttga", StringComparison.OrdinalIgnoreCase) ||
                             c.Name.Equals("icon", StringComparison.OrdinalIgnoreCase)));
                    }

                    // Nếu phát hiện là Series, đánh dấu vào _hiddenAppIds
                    if (isSeriesNode)
                    {
                        _hiddenAppIds.Add(potentialAppId);
                        _logger.LogDebug("Đã đánh dấu AppID {0} là Series để bỏ qua", potentialAppId);
                    }

                    // SteamKit2.KeyValue không có thuộc tính Parent, nên cần tiếp cận khác
                    // Thay vì kiểm tra qua node cha, kiểm tra qua tên node
                    if (!isAppIdNode)
                    {
                        // Kiểm tra thêm dựa trên tên node và các đặc điểm khác
                        if (potentialAppId > 100 && // AppID hợp lệ thường lớn hơn 100
                            node.Children != null && node.Children.Count > 0)
                        {
                            // Các node con có tên đặc trưng của game/app
                            var commonChildNames = new[] { "name", "type", "config", "dlc", "depots", "common" };
                            isAppIdNode = node.Children.Any(c => c != null && c.Name != null &&
                                commonChildNames.Contains(c.Name.ToLower()));
                        }
                    }

                    if (isAppIdNode && !isSeriesNode)
                    {
                        targetAppIdSet.Add(potentialAppId);
                        _logger.LogDebug("Đã tìm thấy AppID {0} là tên node trong package {1}{2}",
                            potentialAppId, packageId, sourceTag);
                    }

                    // Kiểm tra thêm dựa trên đặc điểm của node và node con
                    if (!isAppIdNode && !isSeriesNode && node.Children != null && node.Children.Count > 1)
                    {
                        // Kiểm tra số lượng node con có cấu trúc tương tự AppID
                        int similarChildrenCount = 0;
                        int totalChildrenCount = 0;

                        foreach (var child in node.Children)
                        {
                            if (child == null) continue;
                            totalChildrenCount++;

                            if (uint.TryParse(child.Name, out uint siblingId) &&
                                siblingId > 0 && siblingId < 5000000)
                            {
                                similarChildrenCount++;
                            }
                        }

                        // Nếu phần lớn các node con đều là số và nằm trong phạm vi AppID
                        if (totalChildrenCount > 0 && similarChildrenCount > 0 &&
                            ((double)similarChildrenCount / totalChildrenCount >= 0.5))
                        {
                            targetAppIdSet.Add(potentialAppId);
                            _logger.LogDebug("Đã tìm thấy AppID {0} qua phân tích cấu trúc trong package {1}{2}",
                                potentialAppId, packageId, sourceTag);
                        }
                    }
                }

                // Trường hợp đặc biệt: kiểm tra các node có thể chứa appid
                if (node.Name != null && node.Children != null &&
                    (node.Name.Equals("app", StringComparison.OrdinalIgnoreCase) ||
                     node.Name.Equals("dlc", StringComparison.OrdinalIgnoreCase) ||
                     node.Name.Equals("depots", StringComparison.OrdinalIgnoreCase) ||
                     node.Name.Equals("extended", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var child in node.Children)
                    {
                        if (child == null || child.Children == null) continue;

                        // Kiểm tra node con có tên "appid"
                        KeyValue appidNode = null;
                        foreach (var subChild in child.Children)
                        {
                            if (subChild != null && subChild.Name != null &&
                                subChild.Name.Equals("appid", StringComparison.OrdinalIgnoreCase))
                            {
                                appidNode = subChild;
                                break;
                            }
                        }

                        if (appidNode != null && appidNode.Value != null &&
                            uint.TryParse(appidNode.Value.ToString(), out uint appId) &&
                            appId > 0 && appId < 5000000)
                        {
                            targetAppIdSet.Add(appId);
                            _logger.LogDebug("Đã tìm thấy AppID {0} từ node đặc biệt trong package {1}{2}",
                                appId, packageId, sourceTag);
                        }
                    }
                }

                // Duyệt đệ quy qua tất cả node con nếu có
                if (node.Children != null)
                {
                    foreach (var child in node.Children)
                    {
                        if (child != null)
                        {
                            FindAppIdsRecursively(child, packageId, isFromFamilySharing);
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
                // Thêm biến đếm thời gian để phá vỡ vòng lặp nếu quá lâu
                var processingStartTime = DateTime.Now;
                var maxProcessingTime = TimeSpan.FromMinutes(5); // Tối đa 5 phút

                // Kết hợp các AppID từ cả sở hữu và Family Sharing
                HashSet<uint> allAppIds = new HashSet<uint>(_ownedAppIds);
                foreach (var appId in _familySharedAppIds)
                {
                    allAppIds.Add(appId);
                }
                foreach (var appId in _hiddenAppIds)
                {
                    allAppIds.Add(appId);
                }

                // Thêm thông tin tổng quan về số lượng game đã tìm thấy
                _logger.LogInformation("Đã tìm thấy tổng cộng {0} AppID (sở hữu: {1}, Family Sharing: {2}, ẩn: {3}), đang lấy thông tin chi tiết...",
                allAppIds.Count, _ownedAppIds.Count, _familySharedAppIds.Count, _hiddenAppIds.Count);

                if (allAppIds.Count == 0)
                {
                    _logger.LogWarning("Không có AppID nào để xử lý");
                    _appInfoEvent.Set();
                    return;
                }

                // Lọc bỏ các AppID không hợp lệ (từ 0 đến 9 là component của Steam)
                allAppIds.RemoveWhere(id => id >= 0 && id <= 9);

                // Giảm số lượng ứng dụng xử lý xuống tối đa 300 để tránh timeout
                var appIdsList = allAppIds.ToList();
                if (appIdsList.Count > 300)
                {
                    _logger.LogWarning("Quá nhiều AppID ({0}), giới hạn xuống 300 để tránh timeout", appIdsList.Count);
                    appIdsList = appIdsList.Take(300).ToList();
                }

                // Chia thành các batch nhỏ (tối đa 50 app mỗi lần thay vì 100)
                int batchSize = 50;
                for (int i = 0; i < appIdsList.Count; i += batchSize)
                {
                    // Kiểm tra nếu đã xử lý quá lâu thì dừng
                    if (DateTime.Now - processingStartTime > maxProcessingTime)
                    {
                        _logger.LogWarning("Đã vượt quá thời gian xử lý tối đa ({0} phút), dừng quá trình và tiếp tục với dữ liệu đã có",
                            maxProcessingTime.TotalMinutes);
                        break;
                    }

                    var batch = appIdsList.Skip(i).Take(batchSize).ToList();
                    _logger.LogInformation("Gửi yêu cầu cho batch {0}-{1}/{2} ứng dụng...",
                        i + 1, Math.Min(i + batchSize, appIdsList.Count), appIdsList.Count);

                    try
                    {
                        // Tạo PICSRequest objects cho SteamKit 3.1.0
                        var appRequests = new List<SteamApps.PICSRequest>();
                        foreach (var id in batch)
                        {
                            appRequests.Add(new SteamApps.PICSRequest(id));
                        }

                        // Gửi yêu cầu
                        _steamApps.PICSGetProductInfo(
                            apps: appRequests,
                            packages: new List<SteamApps.PICSRequest>()
                        );

                        // Đợi một chút để tránh rate limit - tăng từ 300ms lên 500ms
                        Thread.Sleep(500);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Lỗi khi xử lý batch {0}-{1}: {2}",
                            i + 1, Math.Min(i + batchSize, appIdsList.Count), ex.Message);
                        // Đợi thêm thời gian nếu gặp lỗi
                        Thread.Sleep(1000);
                    }
                }

                // Giảm timeout xuống để không chờ quá lâu - từ 2 phút xuống 60 giây
                var reducedTimeout = TimeSpan.FromSeconds(60);
                // Đặt timeout để đảm bảo không bị treo
                Task.Run(() => {
                    if (!_appInfoEvent.WaitOne((int)reducedTimeout.TotalMilliseconds))
                    {
                        _logger.LogWarning("Hết thời gian chờ xử lý app ({0} giây), tiếp tục với danh sách đã có",
                            reducedTimeout.TotalSeconds);
                        _logger.LogInformation("Đã nhận được thông tin cho {0} app", _appInfoCache.Count);
                        // Set event để giải phóng các luồng đang chờ
                        _appInfoEvent.Set();
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
                _logger.LogInformation("Already connected to Steam, reusing existing connection");
                return true;
            }

            try
            {
                // Ensure any previous connection is properly closed
                if (_steamClient.IsConnected)
                {
                    _logger.LogInformation("Disconnecting existing Steam connection before reconnecting");
                    _steamClient.Disconnect();
                    await Task.Delay(500); // Brief delay to ensure disconnection is processed
                }

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

                    // Chỉ lưu trữ thông tin đăng nhập sau khi đã mã hóa
                    // Kiểm tra xem thông tin đăng nhập đã được mã hóa chưa
                    bool isUsernameEncrypted = false;
                    bool isPasswordEncrypted = false;

                    try
                    {
                        // Thử giải mã để kiểm tra xem đã mã hóa chưa
                        _encryptionService.Decrypt(username);
                        // Nếu giải mã thành công mà không có lỗi, nghĩa là chuỗi đã được mã hóa
                        isUsernameEncrypted = true;
                    }
                    catch
                    {
                        // Không giải mã được, tức là chưa mã hóa
                        isUsernameEncrypted = false;
                    }

                    try
                    {
                        _encryptionService.Decrypt(password);
                        isPasswordEncrypted = true;
                    }
                    catch
                    {
                        isPasswordEncrypted = false;
                    }

                    // Lưu thông tin đang nhập đã mã hóa để sử dụng sau này
                    _currentUsername = isUsernameEncrypted ? username : _encryptionService.Encrypt(username);
                    _currentPassword = isPasswordEncrypted ? password : _encryptionService.Encrypt(password);
                    _authCode = authCode;
                    _twoFactorCode = twoFactorCode;

                    var loginDetails = new SteamUser.LogOnDetails
                    {
                        Username = username, // Sử dụng thông tin đăng nhập chưa mã hóa
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

                    string usernameHint = username.Length > 3 ? username.Substring(0, 3) + "***" : "***";
                    _logger.LogInformation("Đang đăng nhập vào Steam với tài khoản {Username}...", usernameHint);
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

            // Thêm biến theo dõi tổng thời gian xử lý
            var totalProcessingStartTime = DateTime.Now;
            var maxTotalProcessingTime = TimeSpan.FromMinutes(10); // Tối đa 10 phút

            // Reset các biến trạng thái
            _ownedAppIds.Clear();
            _familySharedAppIds.Clear();
            _hiddenAppIds.Clear();
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

            // Giảm thời gian chờ licenses xuống 90 giây thay vì 2 phút
            var reducedLicenseTimeout = TimeSpan.FromSeconds(90);

            // Đợi xử lý license với timeout
            bool licensesProcessed = _licenseListEvent.WaitOne((int)reducedLicenseTimeout.TotalMilliseconds);
            if (!licensesProcessed)
            {
                _logger.LogWarning("Timeout khi lấy và xử lý licenses ({0} giây), thử gửi lại yêu cầu...", reducedLicenseTimeout.TotalSeconds);
                // Thử gửi lại yêu cầu
                _licenseListEvent.Reset();
                _steamClient.Send(licenseMsg);
                licensesProcessed = _licenseListEvent.WaitOne((int)reducedLicenseTimeout.TotalMilliseconds);

                if (!licensesProcessed)
                {
                    _logger.LogWarning("Vẫn timeout khi xử lý licenses ({0} giây), tiếp tục với AppID đã thu thập được", reducedLicenseTimeout.TotalSeconds);
                    // Đảm bảo tiếp tục với số lượng AppID đã có
                    _isProcessingPackages = false;
                    if (_ownedAppIds.Count > 0)
                    {
                        _logger.LogInformation("Đã tìm thấy {0} AppID, tiếp tục xử lý", _ownedAppIds.Count);
                        ProcessAppIds();
                    }
                }
            }

            // Giảm thời gian chờ appInfo xuống 60 giây thay vì 2 phút
            var reducedAppInfoTimeout = TimeSpan.FromSeconds(60);

            // Đợi xử lý thông tin app với timeout
            bool appsProcessed = _appInfoEvent.WaitOne((int)reducedAppInfoTimeout.TotalMilliseconds);
            if (!appsProcessed)
            {
                _logger.LogWarning("Timeout khi lấy thông tin app ({0} giây), sử dụng {1} app đã có trong cache",
                    reducedAppInfoTimeout.TotalSeconds, _appInfoCache.Count);
            }

            // Kiểm tra nếu đã vượt quá thời gian tối đa xử lý
            if (DateTime.Now - totalProcessingStartTime > maxTotalProcessingTime)
            {
                _logger.LogWarning("Đã vượt quá thời gian xử lý tối đa ({0} phút), tiếp tục với dữ liệu đã có",
                    maxTotalProcessingTime.TotalMinutes);
            }

            // Tạo kết quả kết hợp cả 3 loại game
            var results = new List<(string AppId, string GameName)>();

            // Xử lý các game sở hữu
            foreach (var appId in _ownedAppIds)
            {
                if (ProcessAppIdForResults(appId, results, false, false))
                {
                    continue;
                }
            }

            // Xử lý các game từ Family Sharing (thêm prefix "[FS]" vào tên game)
            foreach (var appId in _familySharedAppIds)
            {
                // Bỏ qua nếu đã có trong danh sách game sở hữu
                if (_ownedAppIds.Contains(appId))
                {
                    continue;
                }

                if (ProcessAppIdForResults(appId, results, true, false))
                {
                    continue;
                }
            }

            // Lọc bỏ Series trước khi trả về kết quả
            var seriesAppIds = new HashSet<string>();
            var filteredResults = new List<(string AppId, string GameName)>();

            foreach (var result in results)
            {
                string appId = result.AppId;
                string name = result.GameName;

                // Kiểm tra nếu tên game có dấu hiệu là Series
                bool isSeries = false;

                // Kiểm tra tên game có chứa các từ khóa của Series
                if (name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Season", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Episode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (name.Contains(":") && name.Contains("Part")))
                {
                    isSeries = true;
                    seriesAppIds.Add(appId);
                    _logger.LogDebug("Phát hiện Series từ tên game: AppID={0}, Name={1}", appId, name);
                }

                // Kiểm tra nếu AppID thuộc danh sách _hiddenAppIds
                if (uint.TryParse(appId, out uint numAppId) && _hiddenAppIds.Contains(numAppId))
                {
                    isSeries = true;
                    seriesAppIds.Add(appId);
                    _logger.LogDebug("Phát hiện Series từ _hiddenAppIds: AppID={0}, Name={1}", appId, name);
                }

                // Nếu không phải Series, thêm vào kết quả cuối cùng
                if (!isSeries)
                {
                    filteredResults.Add(result);
                }
            }

            _logger.LogInformation("Đã lọc bỏ {0} Series từ tổng số {1} game", seriesAppIds.Count, results.Count);

            // Kiểm tra tổng thể thông tin đã nhận được
            _logger.LogInformation("Đã lấy được {0} game từ tài khoản Steam (sở hữu: {1}, Family Sharing: {2}, ẩn: {3}), cache có {4} app info",
                filteredResults.Count, _ownedAppIds.Count, _familySharedAppIds.Count, _hiddenAppIds.Count, _appInfoCache.Count);

            // Ngắt kết nối để giải phóng tài nguyên
            if (_steamClient.IsConnected)
            {
                _logger.LogInformation("Disconnecting Steam session after retrieving games");
                if (_isLoggedIn)
                {
                    _steamUser.LogOff();
                    _isLoggedIn = false;
                }
                _steamClient.Disconnect();
                _isConnected = false;
            }

            return filteredResults;
        }

        // Phương thức hỗ trợ để xử lý AppID và thêm vào kết quả
        private bool ProcessAppIdForResults(uint appId, List<(string AppId, string GameName)> results, bool isFromFamilySharing, bool isHidden)
        {
            // Bỏ qua các appId từ 0 đến 9 vì là component của Steam
            if (appId >= 0 && appId <= 9)
            {
                return true;
            }

            // Cố gắng lấy tên game từ cache
            string name = "Unknown";
            bool shouldSkip = false;
            string skipReason = "";

            KeyValue common = null;
            if (_appInfoCache.TryGetValue(appId, out var appInfo) && appInfo?.KeyValues != null)
            {
                try
                {
                    common = appInfo.KeyValues["common"];
                }
                catch
                {
                    // Bỏ qua lỗi nếu không tìm thấy node common
                }
                if (common != null)
                {
                    var nameNode = common["name"];
                    if (nameNode != null && nameNode.Value != null)
                    {
                        name = nameNode.Value.ToString();
                    }

                    // Kiểm tra loại ứng dụng
                    var typeNode = common["type"];
                    if (typeNode != null && typeNode.Value != null)
                    {
                        string appType = typeNode.Value.ToString();

                        // Lọc bỏ ứng dụng có loại "Video", "Tool", "Media", "Legacy Media" và "Series"
                        if (appType.Equals("Video", StringComparison.OrdinalIgnoreCase) ||
                            appType.Equals("Tool", StringComparison.OrdinalIgnoreCase) ||
                            appType.Equals("DLC", StringComparison.OrdinalIgnoreCase) ||
                            appType.Equals("Application", StringComparison.OrdinalIgnoreCase) ||
                            appType.Equals("Media", StringComparison.OrdinalIgnoreCase) ||
                            appType.Equals("Demo", StringComparison.OrdinalIgnoreCase) ||
                            appType.Equals("Legacy Media", StringComparison.OrdinalIgnoreCase) ||
                            appType.Equals("Series", StringComparison.OrdinalIgnoreCase))
                        {
                            skipReason = appType;
                            shouldSkip = true;
                        }
                    }
                }
            }

            // Thêm kiểm tra bằng cách phân tích tên và các thuộc tính khác
            if (!shouldSkip && !string.IsNullOrEmpty(name))
            {
                // Kiểm tra tên có chứa các từ khóa của tool/video/series
                if (name.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("trailer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("editor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("server", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("sdk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("dedicated", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("series", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("season", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("episode", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // Kiểm tra thêm các dấu hiệu của tool/video/series thông qua các thuộc tính khác
                    bool isTool = false;

                    // Kiểm tra categoryNodes trong KeyValues - đối với SteamKit 3.1.0
                    KeyValue categoryNodes = null;
                    if (common == null)
                    {
                        return false;
                    }

                    try
                    {
                        // Tìm kiếm node category hoặc categories
                        categoryNodes = common["category"] ?? common["categories"];
                    }
                    catch
                    {
                        // Bỏ qua nếu không tìm thấy
                        categoryNodes = null;
                    }
                    if (categoryNodes != null && categoryNodes.Children != null)
                    {
                        foreach (var category in categoryNodes.Children)
                        {
                            if (category != null && category.Value != null && (
                                category.Value.ToString().IndexOf("tool", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                category.Value.ToString().IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                category.Value.ToString().IndexOf("media", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                category.Value.ToString().IndexOf("series", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                category.Value.ToString().IndexOf("episodic", StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                isTool = true;
                                break;
                            }
                        }
                    }

                    if (isTool)
                    {
                        skipReason = "Tool/Video/Series (from name/categories)";
                        shouldSkip = true;
                    }
                }

                // Kiểm tra thêm cho Series
                if (name.Contains(":") &&
                   (name.Contains("Episode") || name.Contains("Season") || name.Contains("Chapter") ||
                    name.Contains("Part") || name.Contains("Series")))
                {
                    skipReason = "Series (from name pattern)";
                    shouldSkip = true;
                }
            }

            // Nếu là ứng dụng thuộc loại cần bỏ qua, không thêm vào kết quả
            if (shouldSkip)
            {
                _logger.LogDebug("Bỏ qua ứng dụng loại {Type}: AppID {AppId}, Tên: {Name}", skipReason, appId, name);
                return true;
            }

            if (name == "Unknown")
            {
                name = $"Game {appId}";
            }

            // Thêm prefix cho game từ Family Sharing hoặc game ẩn
            if (isFromFamilySharing)
            {
                name = $"[FS] {name}";
            }
            else if (isHidden)
            {
                name = $"[Ẩn] {name}";
            }

            // Kiểm tra nếu tên game có định dạng "Game X" thì bỏ qua
            if (name.StartsWith("Game ") && !name.StartsWith("[FS] Game ") && !name.StartsWith("[Ẩn] Game ") && int.TryParse(name.Substring(5), out _))
            {
                return true;
            }

            results.Add((appId.ToString(), name));
            return false;
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
                        bool isSeries = false;

                        if (_appInfoCache.TryGetValue(id, out var appInfo) && appInfo?.KeyValues != null)
                        {
                            var common = appInfo.KeyValues?["common"];
                            if (common != null)
                            {
                                var nameNode = common["name"];
                                if (nameNode != null && nameNode.Value != null)
                                {
                                    name = nameNode.Value.ToString();
                                }

                                // Kiểm tra xem có phải là Series không
                                var typeNode = common["type"];
                                if (typeNode != null && typeNode.Value != null)
                                {
                                    string appType = typeNode.Value.ToString();
                                    if (appType.Equals("Series", StringComparison.OrdinalIgnoreCase))
                                    {
                                        isSeries = true;
                                        _logger.LogDebug("Bỏ qua Series: AppID={0}, Type={1}", id, appType);
                                        continue;
                                    }
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

                        // Kiểm tra nếu tên game có dấu hiệu là Series
                        if (name.IndexOf("Series", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Season", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Episode", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            (name.Contains(":") && (name.Contains("Part") || name.Contains("Chapter"))))
                        {
                            _logger.LogDebug("Bỏ qua Series từ tên: AppID={0}, Name={1}", id, name);
                            continue;
                        }

                        // Kiểm tra nếu thuộc danh sách _hiddenAppIds
                        if (_hiddenAppIds.Contains(id))
                        {
                            _logger.LogDebug("Bỏ qua ứng dụng từ danh sách ẩn: AppID={0}, Name={1}", id, name);
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

        public void ClearCache()
        {
            _logger.LogInformation("Đang xóa cache thông tin ứng dụng Steam...");
            _appInfoCache.Clear();
            _licenses.Clear();
            _ownedAppIds.Clear();
            _familySharedAppIds.Clear();
            _hiddenAppIds.Clear();
            _logger.LogInformation("Đã xóa sạch cache thông tin ứng dụng Steam");
        }

        public async Task<List<(string AppId, string GameName)>> ScanAccountGames(string username, string password)
        {
            // First, ensure we're properly disconnected from any previous account
            try
            {
                if (_steamClient != null && _steamClient.IsConnected)
                {
                    _logger.LogInformation("Disconnecting previous Steam session before scanning new account");
                    if (_isLoggedIn)
                    {
                        _steamUser.LogOff();
                        _isLoggedIn = false;
                    }
                    _steamClient.Disconnect();
                    _isConnected = false;
                    // Wait briefly to ensure disconnection is processed
                    await Task.Delay(500);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during logout from previous account: {Message}", ex.Message);
            }

            // Reset state variables
            _currentUsername = null;
            _currentPassword = null;
            _authCode = null;
            _twoFactorCode = null;
            _isWaitingFor2FA = false;

            // Xóa cache trước khi bắt đầu quét
            ClearCache();

            // Đơn giản hóa - gọi thẳng đến hàm xử lý chính
            try
            {
                string usernameHint = !string.IsNullOrEmpty(username) && username.Length > 3 ?
                    username.Substring(0, 3) + "***" : "***";
                _logger.LogInformation("Đang quét danh sách game cho tài khoản {UsernameHint}...", usernameHint);

                // Kiểm tra xem username và password có được mã hóa không, nếu có thì giải mã
                string decodedUsername = username;
                string decodedPassword = password;
                bool usernameDecrypted = false;
                bool passwordDecrypted = false;

                // Log chi tiết về dữ liệu đầu vào để debug
                _logger.LogDebug("ScanAccountGames: Độ dài username: {UsernameLength}, Độ dài password: {PasswordLength}",
                    username?.Length ?? 0, password?.Length ?? 0);

                // Log giá trị hash của chuỗi đầu vào để debug (không log giá trị thực)
                if (!string.IsNullOrEmpty(username))
                {
                    _logger.LogDebug("ScanAccountGames: Username hash: {Hash}",
                        Convert.ToBase64String(System.Security.Cryptography.MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(username))));
                }

                // Thử phát hiện chuỗi đã mã hóa dựa vào các đặc điểm như độ dài, ký tự đặc biệt, v.v.
                bool isUsernameLikelyEncrypted = !string.IsNullOrEmpty(username) &&
                    username.Length > 20 &&
                    username.Contains("=") &&
                    !username.Contains(" ");

                bool isPasswordLikelyEncrypted = !string.IsNullOrEmpty(password) &&
                    password.Length > 20 &&
                    password.Contains("=") &&
                    !password.Contains(" ");

                _logger.LogDebug("ScanAccountGames: Phát hiện sơ bộ - Username có vẻ đã mã hóa: {Encrypted}, Password có vẻ đã mã hóa: {Encrypted}",
                    isUsernameLikelyEncrypted, isPasswordLikelyEncrypted);

                // Thử giải mã username
                if (!string.IsNullOrEmpty(username))
                {
                    try
                    {
                        string originalUsername = username;
                        decodedUsername = _encryptionService.Decrypt(username);
                        usernameDecrypted = true;
                        string decryptedHint = decodedUsername.Length > 3 ?
                            decodedUsername.Substring(0, 3) + "***" : "***";

                        _logger.LogDebug("ScanAccountGames: Đã giải mã username thành công: hash={Original} -> hash={Decrypted}",
                            Convert.ToBase64String(System.Security.Cryptography.MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(originalUsername))),
                            Convert.ToBase64String(System.Security.Cryptography.MD5.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(decodedUsername))));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("ScanAccountGames: Username không cần giải mã hoặc không thể giải mã: {Error}", ex.Message);
                        // Giữ nguyên username gốc nếu không thể giải mã
                    }
                }

                // Thử giải mã password
                if (!string.IsNullOrEmpty(password))
                {
                    try
                    {
                        decodedPassword = _encryptionService.Decrypt(password);
                        passwordDecrypted = true;
                        _logger.LogDebug("ScanAccountGames: Đã giải mã password thành công");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("ScanAccountGames: Password không cần giải mã hoặc không thể giải mã: {Error}", ex.Message);
                        // Giữ nguyên password gốc nếu không thể giải mã
                    }
                }

                _logger.LogInformation("ScanAccountGames: Trạng thái giải mã - Username: {UsernameDecrypted}, Password: {PasswordDecrypted}, Có vẻ đã mã hóa: Username={UsernameEncrypted}, Password={PasswordEncrypted}",
                    usernameDecrypted, passwordDecrypted, isUsernameLikelyEncrypted, isPasswordLikelyEncrypted);

                // Kiểm tra tính hợp lệ trước khi thử đăng nhập
                if (string.IsNullOrEmpty(decodedUsername) || string.IsNullOrEmpty(decodedPassword))
                {
                    _logger.LogError("ScanAccountGames: Username hoặc password sau khi giải mã là rỗng, không thể đăng nhập");
                    throw new Exception("Thông tin đăng nhập không hợp lệ sau khi giải mã");
                }

                string decodedUsernameHint = decodedUsername.Length > 3 ?
                    decodedUsername.Substring(0, 3) + "***" : "***";
                _logger.LogInformation("ScanAccountGames: Đang gọi GetOwnedGamesAsync với username: {UsernameHint}",
                    decodedUsernameHint);

                return await GetOwnedGamesAsync(decodedUsername, decodedPassword);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi quét game từ tài khoản: {Error}", ex.Message);
                throw;
            }
        }

        // Sửa lại IsPackageFromFamilySharing để ép kiểu LicenseType khi so sánh
        private bool IsPackageFromFamilySharing(uint packageId)
        {
            // Kiểm tra dựa vào các thuộc tính khác thay vì dùng flag
            foreach (var license in _licenses)
            {
                if (license.PackageID == packageId)
                {
                    // Các tiêu chí để xác định một package từ Family Sharing:
                    // 1. PaymentMethod có giá trị 0 hoặc 10 (FreeOnDemand)
                    // 2. LicenseType thường có giá trị 1 cho license được chia sẻ
                    // Sử dụng ép kiểu (int) khi so sánh với enum - cải thiện việc phát hiện các license từ Family Sharing
                    if (((int)license.PaymentMethod == 0 ||
                         (int)license.PaymentMethod == 10 ||
                         (int)license.PaymentMethod == 11) && // Thêm loại 11 (FamilySharing)
                        ((int)license.LicenseType == 1 ||
                         (int)license.LicenseType == 0 ||
                         (int)license.LicenseType == 4)) // Thêm loại 4 (có thể là Family Sharing)
                    {
                        _logger.LogDebug("Phát hiện license Family Sharing: PaymentMethod={0}, LicenseType={1}, PackageID={2}",
                            (int)license.PaymentMethod, (int)license.LicenseType, license.PackageID);
                        return true;
                    }
                }
            }

            return false;
        }
    }
}