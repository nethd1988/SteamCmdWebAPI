using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SteamCmdWebAPI.Services
{
    public class LicenseService : IDisposable
    {
        private readonly ILogger<LicenseService> _logger;
        private readonly HttpClient _httpClient;
        private const string LICENSE_CACHE_FILE = "license_cache.json";

        // Thêm thuộc tính để lưu trữ thông tin license hiện tại
        private ViewLicenseDto _currentLicense;

        // Thông tin API license
        private const string API_URL = "http://127.0.0.1:60999/api/thirdparty/license";
        private const string API_KEY = "HxU40My7KJNzMoElWqY5LwRvYk6nUwGc";
        private const string AES_IV = "M9z24zymNgrwCtIM";
        private const string AES_KEY = "8Caz082kLMVKnl6OZqeBjgIXmQizbX2d";

        // Thời gian grace period (30 phút)
        private readonly TimeSpan GRACE_PERIOD = TimeSpan.FromMinutes(30);

        // Số lần thử lại
        private const int MAX_RETRY_COUNT = 5;
        private const int RETRY_DELAY_MS = 5000; // 5 giây

        public LicenseService(ILogger<LicenseService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", API_KEY);
        }

        public async Task<LicenseValidationResult> ValidateLicenseAsync()
        {
            // Cố gắng lấy license từ API
            var apiLicense = await GetLicenseWithRetryAsync();

            // Nếu không lấy được, thử đọc từ cache
            if (apiLicense == null)
            {
                _logger.LogWarning("Không thể kết nối tới máy chủ cấp phép, đang kiểm tra license cache...");
                var cachedLicense = LoadLicenseFromCache();

                if (cachedLicense != null)
                {
                    // Tính thời gian grace period
                    var timeSinceLastValidation = DateTime.Now - cachedLicense.LastValidationTime;

                    if (timeSinceLastValidation <= GRACE_PERIOD)
                    {
                        _logger.LogInformation("Sử dụng license cache trong thời gian grace period ({0} phút)",
                            GRACE_PERIOD.TotalMinutes);

                        _currentLicense = new ViewLicenseDto
                        {
                            LicenseId = cachedLicense.LicenseId,
                            Active = cachedLicense.Active,
                            DayLeft = cachedLicense.DayLeft,
                            Expires = cachedLicense.Expires,
                            Status = cachedLicense.Status,
                            WksLimit = cachedLicense.WksLimit
                        };

                        return new LicenseValidationResult
                        {
                            IsValid = true,
                            Message = $"Sử dụng license cache. Hết hạn: {cachedLicense.Expires:dd/MM/yyyy}",
                            UsingCache = true,
                            License = _currentLicense
                        };
                    }
                    else
                    {
                        _logger.LogWarning("License cache đã quá thời gian grace period ({0} phút)",
                            GRACE_PERIOD.TotalMinutes);
                    }
                }

                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Không thể kết nối tới máy chủ cấp phép và không có license cache hợp lệ"
                };
            }

            var license = apiLicense.ViewLicense;
            _currentLicense = license; // Lưu license hiện tại

            // Kiểm tra tính hợp lệ của license
            bool isValid = license.Active &&
                           license.Status == 7 &&
                           license.Expires > DateTime.Now;

            if (isValid)
            {
                _logger.LogInformation(
                    "License hợp lệ. Còn {DayLeft} ngày. Hết hạn: {Expires}",
                    license.DayLeft,
                    license.Expires
                );

                // Lưu license vào cache
                SaveLicenseToCache(apiLicense);

                return new LicenseValidationResult
                {
                    IsValid = true,
                    Message = $"Giấy phép còn hiệu lực. Hết hạn: {license.Expires:dd/MM/yyyy}",
                    License = license
                };
            }
            else
            {
                _logger.LogWarning(
                    "License không hợp lệ. Trạng thái: Active={Active}, Status={Status}, Expires={Expires}",
                    license.Active,
                    license.Status,
                    license.Expires
                );

                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Giấy phép không còn hiệu lực"
                };
            }
        }

        private async Task<ThirdPartyLicenseModel> GetLicenseWithRetryAsync()
        {
            for (int attempt = 1; attempt <= MAX_RETRY_COUNT; attempt++)
            {
                try
                {
                    _logger.LogInformation("Đang thử kết nối đến máy chủ cấp phép (lần {0}/{1})...",
                        attempt, MAX_RETRY_COUNT);

                    var license = await GetLicenseFromApiAsync();

                    if (license != null)
                    {
                        _logger.LogInformation("Đã kết nối thành công đến máy chủ cấp phép");
                        return license;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Lỗi khi lấy giấy phép từ API (lần {0}/{1})",
                        attempt, MAX_RETRY_COUNT);
                }

                if (attempt < MAX_RETRY_COUNT)
                {
                    _logger.LogInformation("Đang chờ {0}ms trước khi thử lại...", RETRY_DELAY_MS);
                    await Task.Delay(RETRY_DELAY_MS);
                }
            }

            _logger.LogError("Không thể kết nối đến máy chủ cấp phép sau {0} lần thử", MAX_RETRY_COUNT);
            return null;
        }

        private async Task<ThirdPartyLicenseModel> GetLicenseFromApiAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(API_URL);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("API trả về mã lỗi: {StatusCode}", response.StatusCode);
                    return null;
                }

                var encryptString = await response.Content.ReadAsStringAsync();
                if (string.IsNullOrEmpty(encryptString))
                {
                    _logger.LogWarning("API trả về kết quả rỗng");
                    return null;
                }

                var baseString = Decrypt(encryptString, AES_KEY, AES_IV);
                return JsonConvert.DeserializeObject<ThirdPartyLicenseModel>(baseString);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy giấy phép từ API");
                return null;
            }
        }

        private void SaveLicenseToCache(ThirdPartyLicenseModel license)
        {
            try
            {
                var cacheData = new LicenseCacheData
                {
                    LicenseId = license.ViewLicense.LicenseId,
                    Active = license.ViewLicense.Active,
                    DayLeft = license.ViewLicense.DayLeft,
                    Expires = license.ViewLicense.Expires,
                    Status = license.ViewLicense.Status,
                    WksLimit = license.ViewLicense.WksLimit,
                    LastValidationTime = DateTime.Now
                };

                var json = JsonConvert.SerializeObject(cacheData, Formatting.Indented);
                var encryptedData = Encrypt(json, AES_KEY, AES_IV);

                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string dataDir = Path.Combine(baseDir, "data");
                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }

                string cachePath = Path.Combine(dataDir, LICENSE_CACHE_FILE);
                File.WriteAllText(cachePath, encryptedData);

                _logger.LogInformation("Đã lưu thông tin license vào cache");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lưu license vào cache");
            }
        }

        private LicenseCacheData LoadLicenseFromCache()
        {
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string cachePath = Path.Combine(baseDir, "data", LICENSE_CACHE_FILE);

                if (!File.Exists(cachePath))
                {
                    _logger.LogWarning("Không tìm thấy file cache license");
                    return null;
                }

                var encryptedData = File.ReadAllText(cachePath);
                var json = Decrypt(encryptedData, AES_KEY, AES_IV);

                var cacheData = JsonConvert.DeserializeObject<LicenseCacheData>(json);

                _logger.LogInformation("Đã đọc license từ cache. Hết hạn: {0}, Lần xác thực cuối: {1}",
                    cacheData.Expires, cacheData.LastValidationTime);

                return cacheData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi đọc license từ cache");
                return null;
            }
        }

        private string Decrypt(string encryptedText, string key, string iv)
        {
            if (string.IsNullOrEmpty(encryptedText))
                throw new ArgumentNullException(nameof(encryptedText));

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] ivBytes = Encoding.UTF8.GetBytes(iv);
            Array.Resize(ref keyBytes, 32); // 256 bits
            Array.Resize(ref ivBytes, 16);  // 128 bits

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
                using (var aes = Aes.Create())
                {
                    aes.Key = keyBytes;
                    aes.IV = ivBytes;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                    using (var ms = new MemoryStream(encryptedBytes))
                    using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs))
                    {
                        return sr.ReadToEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi giải mã");
                throw;
            }
        }

        private string Encrypt(string plainText, string key, string iv)
        {
            if (string.IsNullOrEmpty(plainText))
                throw new ArgumentNullException(nameof(plainText));

            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] ivBytes = Encoding.UTF8.GetBytes(iv);
            Array.Resize(ref keyBytes, 32); // 256 bits
            Array.Resize(ref ivBytes, 16);  // 128 bits

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                using (var aes = Aes.Create())
                {
                    aes.Key = keyBytes;
                    aes.IV = ivBytes;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                    using (var ms = new MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (var sw = new StreamWriter(cs))
                        {
                            sw.Write(plainText);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi mã hóa");
                throw;
            }
        }

        // Thêm phương thức để lấy username từ license
        public string GetLicenseUsername()
        {
            try
            {
                if (_currentLicense != null && !string.IsNullOrEmpty(_currentLicense.LicenseId))
                {
                    // License ID có format: username#timestamp#randomhash
                    var parts = _currentLicense.LicenseId.Split('#');
                    if (parts.Length > 0)
                    {
                        string username = parts[0];
                        if (!string.IsNullOrEmpty(username))
                        {
                            _logger.LogInformation("Got username from current license: {Username}", username);
                            return username;
                        }
                    }
                }

                // Nếu chưa có current license, thử load từ cache
                var cachedLicense = LoadLicenseFromCache();
                if (cachedLicense != null && !string.IsNullOrEmpty(cachedLicense.LicenseId))
                {
                    var parts = cachedLicense.LicenseId.Split('#');
                    if (parts.Length > 0)
                    {
                        string username = parts[0];
                        if (!string.IsNullOrEmpty(username))
                        {
                            _logger.LogInformation("Got username from cached license: {Username}", username);
                            return username;
                        }
                    }
                }

                _logger.LogWarning("No username found in license");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting username from license");
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }

    // Kết quả xác thực license
    public class LicenseValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public bool UsingCache { get; set; }
        public ViewLicenseDto License { get; set; } // Thêm thuộc tính License
    }

    // Class lưu cache license
    public class LicenseCacheData
    {
        public string LicenseId { get; set; }
        public bool Active { get; set; }
        public int DayLeft { get; set; }
        public DateTime Expires { get; set; }
        public int WksLimit { get; set; }
        public int Status { get; set; }
        public DateTime LastValidationTime { get; set; }
    }

    // Giữ nguyên các model cần thiết
    public class ViewLicenseDto
    {
        public string LicenseId { get; set; }
        public bool Active { get; set; }
        public int DayLeft { get; set; }
        public DateTime Expires { get; set; }
        public int WksLimit { get; set; }
        public int Status { get; set; }
    }

    public class ThirdPartyLicenseModel
    {
        public ViewLicenseDto ViewLicense { get; set; }
        public int MaxSecondsOffset { get; set; }
        public DateTime CreateTime { get; set; }
    }
}