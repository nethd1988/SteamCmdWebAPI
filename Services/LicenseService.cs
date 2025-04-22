using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace SteamCmdWebAPI.Services
{
    public class LicenseService : IDisposable
    {
        private readonly ILogger<LicenseService> _logger;
        private readonly HttpClient _httpClient;

        // Thông tin API license
        private const string API_URL = "http://127.0.0.1:60999/api/thirdparty/license";
        private const string API_KEY = "HxU40My7KJNzMoElWqY5LwRvYk6nUwGc";
        private const string AES_IV = "M9z24zymNgrwCtIM";
        private const string AES_KEY = "8Caz082kLMVKnl6OZqeBjgIXmQizbX2d";

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
            try
            {
                // Kiểm tra kết nối và lấy thông tin license
                var licenseModel = await GetLicenseFromApiAsync();

                // Nếu không lấy được thông tin license
                if (licenseModel == null)
                {
                    _logger.LogWarning("Không thể lấy thông tin giấy phép");
                    return new LicenseValidationResult
                    {
                        IsValid = false,
                        Message = "Không thể kết nối tới máy chủ cấp phép"
                    };
                }

                var license = licenseModel.ViewLicense;

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

                    return new LicenseValidationResult
                    {
                        IsValid = true,
                        Message = $"Giấy phép còn hiệu lực. Hết hạn: {license.Expires:dd/MM/yyyy}"
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xác thực giấy phép");
                return new LicenseValidationResult
                {
                    IsValid = false,
                    Message = "Lỗi hệ thống khi xác thực giấy phép"
                };
            }
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