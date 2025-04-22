using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public class LicenseService : IDisposable
    {
        private readonly string _apiUrl;
        private readonly string _apiKey;
        private readonly string _aesIv;
        private readonly string _aesKey;
        private readonly HttpClient _httpClient;
        private readonly ILogger<LicenseService> _logger;

        public LicenseService(ILogger<LicenseService> logger)
        {
            _logger = logger;
            _apiUrl = "http://127.0.0.1:60999/api/thirdparty/license";
            _apiKey = "HxU40My7KJNzMoElWqY5LwRvYk6nUwGc";
            _aesIv = "M9z24zymNgrwCtIM";
            _aesKey = "8Caz082kLMVKnl6OZqeBjgIXmQizbX2d";
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
        }

        public async Task<ResponseResult> ValidateLicenseAsync()
        {
            ResponseResult responseResult = new ResponseResult();
            try
            {
                var licenseModel = await GetLicenseFromApiAsync().ConfigureAwait(false);
                if (licenseModel == null)
                {
                    _logger.LogWarning("Không thể lấy thông tin giấy phép");
                    return responseResult;
                }

                if ((DateTime.UtcNow - licenseModel.CreateTime).TotalSeconds > licenseModel.MaxSecondsOffset)
                {
                    _logger.LogWarning("Thời gian giấy phép vượt quá giới hạn");
                    return responseResult;
                }

                var license = licenseModel.ViewLicense;
                bool isValid = license.Active && license.Status == 7 && license.Expires > DateTime.Now;

                if (isValid)
                {
                    _logger.LogInformation("Xác thực giấy phép thành công. Còn {DayLeft} ngày", license.DayLeft);
                }
                else
                {
                    _logger.LogWarning("Xác thực giấy phép thất bại: Active={Active}, Status={Status}, Expires={Expires}", 
                        license.Active, license.Status, license.Expires);
                }
                responseResult.Success = isValid;
                responseResult.License = license;
                return responseResult;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Lỗi kết nối khi xác thực giấy phép");
                return responseResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xác thực giấy phép");
                return responseResult;
            }
        }

        private async Task<ThirdPartyLicenseModel> GetLicenseFromApiAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(_apiUrl).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("API trả về mã lỗi: {StatusCode}", response.StatusCode);
                    return null;
                }

                var encryptString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(encryptString))
                {
                    _logger.LogWarning("API trả về kết quả rỗng");
                    return null;
                }

                var baseString = Decrypt(encryptString, _aesKey, _aesIv);
                return JsonConvert.DeserializeObject<ThirdPartyLicenseModel>(baseString);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi lấy giấy phép từ API");
                throw;
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
                using (var aes = new AesCryptoServiceProvider())
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

    public class ResponseResult
    {
        public bool Success { get; set; }
        public ViewLicenseDto License { get; set; }
    }
}