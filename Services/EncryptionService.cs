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
            
            try 
            {
                // Kiểm tra nhanh xem chuỗi có vẻ được mã hóa không
                if (!IsLikelyEncrypted(cipherText))
                {
                    _logger?.LogDebug("Decrypt: Chuỗi đầu vào không có vẻ là chuỗi đã mã hóa, trả về nguyên gốc.");
                    return cipherText; // Trả về nguyên trạng nếu không có vẻ là chuỗi mã hóa
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
                    _logger?.LogDebug("Decrypt: Thử phương pháp giải mã dự phòng cho cipherText length: {Length}", cipherText.Length);
                    
                    // Thử các cách xử lý chuỗi khác nhau trước khi giải mã
                    // 1. Giải mã chuỗi đã được padding đúng
                    string paddedText = EnsureCorrectBase64Padding(cipherText);
                    if (paddedText != cipherText)
                    {
                        string paddedResult = InternalDecrypt(paddedText);
                        if (!string.IsNullOrEmpty(paddedResult))
                        {
                            _logger?.LogInformation("Decrypt: Giải mã thành công với chuỗi được padding lại");
                            return paddedResult;
                        }
                    }
                    
                    // 2. Nếu chuỗi có vẻ bị mã hóa 2 lần, thử giải mã 2 lần
                    try
                    {
                        string decryptedOnce = InternalDecrypt(cipherText);
                        if (!string.IsNullOrEmpty(decryptedOnce) && IsLikelyEncrypted(decryptedOnce))
                        {
                            string decryptedTwice = InternalDecrypt(decryptedOnce);
                            if (!string.IsNullOrEmpty(decryptedTwice))
                            {
                                _logger?.LogInformation("Decrypt: Giải mã thành công với mã hóa hai lần");
                                return decryptedTwice;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("Decrypt: Không thể áp dụng giải mã kép: {Message}", ex.Message);
                    }
                    
                    // 3. Nếu vẫn không thể giải mã, trả về chuỗi gốc
                    _logger?.LogDebug("Decrypt: Không thể giải mã, trả về chuỗi gốc");
                    return cipherText;
                }
                
                _logger?.LogDebug("Decrypt: Giải mã thất bại, trả về chuỗi gốc vì useFallbackOnError=false");
                return cipherText; // Trả về nguyên bản thay vì chuỗi rỗng để tránh mất dữ liệu
            }
            catch (Exception ex) 
            {
                _logger?.LogError(ex, "Decrypt: Lỗi không xử lý được: {Message}", ex.Message);
                return cipherText; // Trả về nguyên bản trong trường hợp lỗi
            }
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
                    _logger?.LogDebug("InternalDecrypt: Chuỗi đầu vào không phải Base64 hợp lệ: {CipherText}", 
                        cipherText.Length > 10 ? cipherText.Substring(0, 10) + "..." : cipherText);
                    return string.Empty;
                }
                
                byte[] cipherBytes;
                try 
                {
                    cipherBytes = Convert.FromBase64String(cipherText);
                }
                catch (FormatException ex)
                {
                    _logger?.LogDebug("InternalDecrypt: Lỗi định dạng Base64: {Message}", ex.Message);
                    return string.Empty;
                }
                
                // Kiểm tra chiều dài đầu vào
                if (cipherBytes.Length == 0)
                {
                    _logger?.LogDebug("InternalDecrypt: Chiều dài dữ liệu mã hóa = 0");
                    return string.Empty;
                }
                
                // Kiểm tra xem chiều dài dữ liệu có đúng block size không
                // AES block size là 16 bytes (128 bits)
                if (cipherBytes.Length % 16 != 0)
                {
                    _logger?.LogDebug("InternalDecrypt: Chiều dài dữ liệu mã hóa không phải bội số của block size (16 bytes): {Length}", cipherBytes.Length);
                    return string.Empty;
                }
                
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
                        try
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
                                    _logger?.LogDebug("InternalDecrypt: Lỗi giải mã CryptographicException: {Message}", ex.Message);
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
                                _logger?.LogDebug("InternalDecrypt: Dữ liệu giải mã không hợp lệ UTF-16");
                                return string.Empty;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug("InternalDecrypt: Lỗi khi giải mã: {Message}", ex.Message);
                            return string.Empty;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("InternalDecrypt: Lỗi không xác định khi giải mã: {Message}", ex.Message);
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Đảm bảo chuỗi Base64 có padding đúng
        /// </summary>
        private string EnsureCorrectBase64Padding(string base64String)
        {
            if (string.IsNullOrEmpty(base64String))
                return base64String;
                
            // Base64 strings should have a length that is a multiple of 4
            int remainder = base64String.Length % 4;
            if (remainder == 0)
                return base64String;
                
            // Add missing padding
            return base64String + new string('=', 4 - remainder);
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