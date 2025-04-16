using System;
using System.Security.Cryptography;
using System.Text;

namespace SteamCmdWebAPI.Services
{
    public class EncryptionService
    {
        private readonly string _encryptionKey;

        public EncryptionService()
        {
            _encryptionKey = "SteamCmdWebSecureKey123!@#$%";
        }

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";

            byte[] clearBytes = Encoding.Unicode.GetBytes(plainText);
            using (Aes encryptor = Aes.Create())
            {
                var salt = new byte[] { 0x49, 0x76, 0x61, 0x6e, 0x20, 0x4d, 0x65, 0x64, 0x76, 0x65, 0x64, 0x65, 0x76 };
                using (var pdb = new Rfc2898DeriveBytes(_encryptionKey, salt, 1000, HashAlgorithmName.SHA256))
                {
                    encryptor.Key = pdb.GetBytes(32);
                    encryptor.IV = pdb.GetBytes(16);

                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, encryptor.CreateEncryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(clearBytes, 0, clearBytes.Length);
                        }
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return "";

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText);
                using (Aes encryptor = Aes.Create())
                {
                    var salt = new byte[] { 0x49, 0x76, 0x61, 0x6e, 0x20, 0x4d, 0x65, 0x64, 0x76, 0x65, 0x64, 0x65, 0x76 };
                    using (var pdb = new Rfc2898DeriveBytes(_encryptionKey, salt, 1000, HashAlgorithmName.SHA256))
                    {
                        encryptor.Key = pdb.GetBytes(32);
                        encryptor.IV = pdb.GetBytes(16);

                        using (MemoryStream ms = new MemoryStream())
                        {
                            using (CryptoStream cs = new CryptoStream(ms, encryptor.CreateDecryptor(), CryptoStreamMode.Write))
                            {
                                cs.Write(cipherBytes, 0, cipherBytes.Length);
                                cs.FlushFinalBlock();
                            }
                            return Encoding.Unicode.GetString(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log lỗi nhưng trả về chuỗi rỗng
                Console.WriteLine("Lỗi giải mã: " + ex.Message);
                return string.Empty;
            }
        }
    }
}