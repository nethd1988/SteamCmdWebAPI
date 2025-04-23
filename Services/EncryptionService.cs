using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace SteamCmdWebAPI.Services
{
    public class EncryptionService
    {
        private readonly string _encryptionKey;
        private readonly string _encryptionIV;

        public EncryptionService(IConfiguration configuration)
        {
            _encryptionKey = configuration["Encryption:Key"] ?? "ThisIsASecretKey1234567890123456";
            _encryptionIV = configuration["Encryption:IV"] ?? "ThisIsAnIV123456";
        }

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            try
            {
                byte[] clearBytes = Encoding.Unicode.GetBytes(plainText);
                using (Aes encryptor = Aes.Create())
                {
                    byte[] keyBytes = Encoding.UTF8.GetBytes(_encryptionKey);
                    byte[] ivBytes = Encoding.UTF8.GetBytes(_encryptionIV);

                    // Đảm bảo đúng độ dài key và IV
                    Array.Resize(ref keyBytes, 32); // 256 bit
                    Array.Resize(ref ivBytes, 16);  // 128 bit

                    encryptor.Key = keyBytes;
                    encryptor.IV = ivBytes;

                    using (var ms = new System.IO.MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, encryptor.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(clearBytes, 0, clearBytes.Length);
                            cs.Close();
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText) || cipherText == "encrypted") return string.Empty;

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText);
                using (Aes encryptor = Aes.Create())
                {
                    byte[] keyBytes = Encoding.UTF8.GetBytes(_encryptionKey);
                    byte[] ivBytes = Encoding.UTF8.GetBytes(_encryptionIV);

                    // Đảm bảo đúng độ dài key và IV
                    Array.Resize(ref keyBytes, 32); // 256 bit
                    Array.Resize(ref ivBytes, 16);  // 128 bit

                    encryptor.Key = keyBytes;
                    encryptor.IV = ivBytes;

                    using (var ms = new System.IO.MemoryStream())
                    {
                        using (var cs = new CryptoStream(ms, encryptor.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(cipherBytes, 0, cipherBytes.Length);
                            cs.Close();
                        }
                        return Encoding.Unicode.GetString(ms.ToArray());
                    }
                }
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}