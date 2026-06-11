using System;
using System.Text;
using System.Security.Cryptography;

namespace Softphone
{
    /// <summary>
    /// Класс для шифрования/расшифровки GitHub токена
    /// Использует AES шифрование для безопасного хранения токена
    /// </summary>
    public static class TokenEncryption
    {
        private const string DpapiPrefix = "dpapi:";

        // Ключ шифрования (можно изменить для дополнительной безопасности)
        // ВАЖНО: Не меняйте этот ключ после того, как токены уже сохранены!
        private static readonly byte[] _key = Encoding.UTF8.GetBytes("Callspire2024GitHubTokenEncryptionKey!@#$%^&*()");
        
        // IV (Initialization Vector) - должен быть 16 байт для AES
        private static readonly byte[] _iv = Encoding.UTF8.GetBytes("1234567890123456");
        
        /// <summary>
        /// Шифрует токен для безопасного хранения
        /// </summary>
        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;
            
            try
            {
                // Prefer OS-bound encryption on Windows (DPAPI). This is significantly safer than a fixed app key.
                try
                {
                    byte[] plainBytesDpapi = Encoding.UTF8.GetBytes(plainText);
                    byte[] protectedBytes = ProtectedData.Protect(plainBytesDpapi, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                    return DpapiPrefix + Convert.ToBase64String(protectedBytes);
                }
                catch
                {
                    // Fall back to legacy AES if DPAPI isn't available for some reason.
                }

                // Обрезаем ключ до 32 байт для AES-256 или до 16 байт для AES-128
                byte[] keyBytes = new byte[32];
                Array.Copy(_key, 0, keyBytes, 0, Math.Min(_key.Length, 32));
                
                using (Aes aes = Aes.Create())
                {
                    aes.Key = keyBytes;
                    aes.IV = _iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    
                    using (ICryptoTransform encryptor = aes.CreateEncryptor())
                    {
                        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                        byte[] encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);
                        return Convert.ToBase64String(encryptedBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[TokenEncryption] Error encrypting token: {ex.Message}");
                return string.Empty;
            }
        }
        
        /// <summary>
        /// Расшифровывает токен
        /// </summary>
        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText))
                return string.Empty;
            
            try
            {
                // DPAPI format
                if (cipherText.StartsWith(DpapiPrefix, StringComparison.Ordinal))
                {
                    var b64 = cipherText.Substring(DpapiPrefix.Length);
                    byte[] protectedBytes = Convert.FromBase64String(b64);
                    byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(plainBytes);
                }

                // Обрезаем ключ до 32 байт для AES-256 или до 16 байт для AES-128
                byte[] keyBytes = new byte[32];
                Array.Copy(_key, 0, keyBytes, 0, Math.Min(_key.Length, 32));
                
                using (Aes aes = Aes.Create())
                {
                    aes.Key = keyBytes;
                    aes.IV = _iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    
                    using (ICryptoTransform decryptor = aes.CreateDecryptor())
                    {
                        byte[] cipherBytes = Convert.FromBase64String(cipherText);
                        byte[] decryptedBytes = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);
                        return Encoding.UTF8.GetString(decryptedBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[TokenEncryption] Error decrypting token: {ex.Message}");
                return string.Empty;
            }
        }
    }
}




