using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SteamCmdWebAPI.Services
{
    public class EncryptionService
    {
        private readonly string _encryptionKey;
        private readonly byte[] _salt = new byte[] { 0x49, 0x76, 0x61, 0x6e, 0x20, 0x4d, 0x65, 0x64, 0x76, 0x65, 0x64, 0x65, 0x76 };

        public EncryptionService()
        {
            _encryptionKey = "SteamCmdWebSecureKey123!@#$%"; // Chìa khóa mã hóa cố định
        }

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            try
            {
                byte[] clearBytes = Encoding.Unicode.GetBytes(plainText);

                using (Aes encryptor = Aes.Create())
                {
                    using (var pdb = new Rfc2898DeriveBytes(_encryptionKey, _salt, 1000, HashAlgorithmName.SHA256))
                    {
                        encryptor.Key = pdb.GetBytes(32);
                        encryptor.IV = pdb.GetBytes(16);

                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (CryptoStream cs = new CryptoStream(ms, encryptor.CreateEncryptor(), CryptoStreamMode.Write))
                            {
                                cs.Write(clearBytes, 0, clearBytes.Length);
                                cs.FlushFinalBlock();
                            }

                            return Convert.ToBase64String(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi mã hóa: {ex.Message}");
                return string.Empty;
            }
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            try
            {
                // Cố gắng chuyển đổi chuỗi base64
                byte[] cipherBytes;
                try
                {
                    cipherBytes = Convert.FromBase64String(cipherText);
                }
                catch (FormatException)
                {
                    Console.WriteLine("Chuỗi không phải là định dạng Base64 hợp lệ");
                    return string.Empty;
                }

                using (Aes encryptor = Aes.Create())
                {
                    // Sử dụng cùng salt và quy trình tạo khóa như khi mã hóa
                    using (var pdb = new Rfc2898DeriveBytes(_encryptionKey, _salt, 1000, HashAlgorithmName.SHA256))
                    {
                        encryptor.Key = pdb.GetBytes(32);
                        encryptor.IV = pdb.GetBytes(16);

                        using (MemoryStream ms = new MemoryStream())
                        {
                            try
                            {
                                using (CryptoStream cs = new CryptoStream(ms, encryptor.CreateDecryptor(), CryptoStreamMode.Write))
                                {
                                    cs.Write(cipherBytes, 0, cipherBytes.Length);
                                    cs.FlushFinalBlock();
                                }

                                return Encoding.Unicode.GetString(ms.ToArray());
                            }
                            catch (CryptographicException ex)
                            {
                                Console.WriteLine($"Lỗi giải mã: {ex.Message}");
                                return string.Empty;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Lỗi giải mã: {ex.Message}");
                return string.Empty;
            }
        }
    }
}