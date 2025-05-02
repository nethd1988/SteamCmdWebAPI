using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading.Tasks;

namespace SteamCmdWebAPI.Services
{
    public class EncryptionService
    {
        private readonly string _encryptionKey;
        private readonly string _encryptionIV;
        private readonly ILogger<EncryptionService> _logger;
        private readonly bool _useFallbackOnError;

        public EncryptionService(IConfiguration configuration, ILogger<EncryptionService> logger = null)
        {
            _logger = logger;
            _encryptionKey = configuration["Encryption:Key"] ?? "ThisIsASecretKey1234567890123456";
            _encryptionIV = configuration["Encryption:IV"] ?? "ThisIsAnIV123456";
            _useFallbackOnError = configuration.GetValue<bool>("Encryption:UseFallbackOnError", true);

            // Log hệ thống mã hóa được khởi tạo
            _logger?.LogInformation("EncryptionService đã được khởi tạo");
        }

        /// <summary>
        /// Mã hóa chuỗi văn bản sử dụng AES-256
        /// </summary>
        /// <param name="plainText">Chuỗi cần mã hóa</param>
        /// <returns>Chuỗi đã mã hóa dạng Base64</returns>
        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                _logger?.LogDebug("Encrypt: Đầu vào rỗng, trả về chuỗi rỗng");
                return string.Empty;
            }

            try
            {
                // Chuẩn bị dữ liệu đầu vào
                byte[] clearBytes = Encoding.Unicode.GetBytes(plainText);
                
                // Tạo đối tượng AES
                using (Aes encryptor = Aes.Create())
                {
                    // Thiết lập cấu hình chuẩn
                    encryptor.Mode = CipherMode.CBC;
                    encryptor.Padding = PaddingMode.PKCS7;
                    
                    // Chuẩn bị key và IV
                    byte[] keyBytes = PrepareKey(_encryptionKey, 32); // 256 bit
                    byte[] ivBytes = PrepareKey(_encryptionIV, 16);   // 128 bit

                    encryptor.Key = keyBytes;
                    encryptor.IV = ivBytes;

                    // Thực hiện mã hóa
                    using (var ms = new MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, encryptor.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(clearBytes, 0, clearBytes.Length);
                            cs.FlushFinalBlock(); // Đảm bảo dữ liệu được ghi hoàn toàn
                        }
                        string result = Convert.ToBase64String(ms.ToArray());
                        _logger?.LogDebug("Encrypt: Mã hóa thành công, output length: {Length}", result.Length);
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Encrypt: Lỗi khi mã hóa dữ liệu: {Message}", ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Giải mã chuỗi đã mã hóa sử dụng AES-256
        /// </summary>
        /// <param name="cipherText">Chuỗi đã mã hóa dạng Base64</param>
        /// <returns>Chuỗi văn bản gốc</returns>
        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText) || cipherText == "encrypted")
            {
                _logger?.LogDebug("Decrypt: Đầu vào rỗng hoặc giá trị đặc biệt, trả về chuỗi rỗng");
                return string.Empty;
            }

            // Thử giải mã theo phương pháp chuẩn 
            string result = InternalDecrypt(cipherText);
            
            // Nếu giải mã thành công, trả về kết quả
            if (!string.IsNullOrEmpty(result))
            {
                return result;
            }
            
            // Thử giải mã với phương pháp dự phòng
            if (_useFallbackOnError)
            {
                _logger?.LogWarning("Decrypt: Thử phương pháp giải mã dự phòng cho cipherText length: {Length}", cipherText.Length);
                
                // Kiểm tra xem cipherText có vẻ như là chuỗi đã mã hóa nữa không
                if (IsLikelyEncrypted(cipherText))
                {
                    try
                    {
                        // Thử giải mã một lần nữa với chuỗi đã giải mã
                        string doubleDecrypted = InternalDecrypt(cipherText);
                        if (!string.IsNullOrEmpty(doubleDecrypted))
                        {
                            _logger?.LogInformation("Decrypt: Giải mã thành công với phương pháp dự phòng");
                            return doubleDecrypted;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("Decrypt: Phương pháp dự phòng thất bại: {Message}", ex.Message);
                    }
                }
                
                // Nếu không thể giải mã, trả về chuỗi gốc
                _logger?.LogWarning("Decrypt: Không thể giải mã, trả về chuỗi gốc");
                return cipherText;
            }
            
            _logger?.LogWarning("Decrypt: Giải mã thất bại, trả về chuỗi rỗng");
            return string.Empty;
        }
        
        /// <summary>
        /// Giải mã nội bộ - thực hiện công việc thực tế
        /// </summary>
        private string InternalDecrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            
            try
            {
                // Kiểm tra chuỗi đầu vào có đúng định dạng Base64 không
                if (!IsValidBase64String(cipherText))
                {
                    _logger?.LogWarning("InternalDecrypt: Chuỗi đầu vào không phải Base64 hợp lệ: {CipherText}", 
                        cipherText.Length > 10 ? cipherText.Substring(0, 10) + "..." : cipherText);
                    return string.Empty;
                }
                
                byte[] cipherBytes = Convert.FromBase64String(cipherText);
                
                using (Aes encryptor = Aes.Create())
                {
                    // Thiết lập cấu hình chuẩn
                    encryptor.Mode = CipherMode.CBC;
                    encryptor.Padding = PaddingMode.PKCS7;
                    
                    // Chuẩn bị key và IV
                    byte[] keyBytes = PrepareKey(_encryptionKey, 32); // 256 bit
                    byte[] ivBytes = PrepareKey(_encryptionIV, 16);   // 128 bit

                    encryptor.Key = keyBytes;
                    encryptor.IV = ivBytes;

                    // Thực hiện giải mã
                    using (var ms = new MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, encryptor.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            try
                            {
                                cs.Write(cipherBytes, 0, cipherBytes.Length);
                                cs.FlushFinalBlock(); // Đảm bảo dữ liệu được ghi hoàn toàn
                            }
                            catch (CryptographicException ex)
                            {
                                _logger?.LogError(ex, "InternalDecrypt: Lỗi giải mã CryptographicException: {Message}", ex.Message);
                                return string.Empty;
                            }
                        }
                        
                        byte[] decryptedBytes = ms.ToArray();
                        string decryptedText = Encoding.Unicode.GetString(decryptedBytes);
                        
                        // Kiểm tra tính hợp lệ của kết quả giải mã
                        if (IsValidUtf16String(decryptedBytes))
                        {
                            _logger?.LogDebug("InternalDecrypt: Giải mã thành công, output length: {Length}", decryptedText.Length);
                            return decryptedText;
                        }
                        else
                        {
                            _logger?.LogWarning("InternalDecrypt: Dữ liệu giải mã không hợp lệ UTF-16");
                            return string.Empty;
                        }
                    }
                }
            }
            catch (FormatException ex)
            {
                _logger?.LogError(ex, "InternalDecrypt: Lỗi định dạng Base64: {Message}", ex.Message);
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "InternalDecrypt: Lỗi không xác định khi giải mã: {Message}", ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Chuẩn bị key hoặc IV với độ dài cố định
        /// </summary>
        private byte[] PrepareKey(string key, int length)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            Array.Resize(ref bytes, length);
            return bytes;
        }
        
        /// <summary>
        /// Kiểm tra xem chuỗi có phải là Base64 hợp lệ không
        /// </summary>
        private bool IsValidBase64String(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
                
            // Chuỗi Base64 phải có độ dài chia hết cho 4
            if (value.Length % 4 != 0)
                return false;
                
            // Chuỗi Base64 chỉ chứa các ký tự hợp lệ
            return System.Text.RegularExpressions.Regex.IsMatch(
                value, @"^[a-zA-Z0-9\+/]*={0,3}$");
        }
        
        /// <summary>
        /// Kiểm tra xem dữ liệu byte có phải là chuỗi UTF-16 hợp lệ không
        /// </summary>
        private bool IsValidUtf16String(byte[] bytes)
        {
            // Kiểm tra cơ bản: UTF-16 phải có số byte chẵn
            if (bytes.Length % 2 != 0)
                return false;
                
            try
            {
                // Thử chuyển đổi về chuỗi và kiểm tra có ký tự không in được không
                string text = Encoding.Unicode.GetString(bytes);
                
                // Kiểm tra có quá nhiều ký tự không in được không
                int nonPrintableCount = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    if (char.IsControl(text[i]) && !char.IsWhiteSpace(text[i]))
                    {
                        nonPrintableCount++;
                    }
                }
                
                // Nếu có hơn 10% ký tự không in được, có thể đây không phải là chuỗi hợp lệ
                return nonPrintableCount <= text.Length * 0.1;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Kiểm tra xem chuỗi có vẻ như đã được mã hóa không
        /// </summary>
        private bool IsLikelyEncrypted(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
                
            // Các đặc điểm của chuỗi đã mã hóa Base64
            return IsValidBase64String(text) && 
                   text.Length >= 24 &&  // Độ dài tối thiểu hợp lý cho chuỗi mã hóa
                   text.Length % 4 == 0; // Độ dài của Base64 luôn chia hết cho 4
        }
    }
}