using System;

namespace Softphone
{
    /// <summary>
    /// Провайдер для получения GitHub токена из зашифрованных настроек
    /// Токен хранится в настройках в зашифрованном виде (AES)
    /// </summary>
    public static class GitHubTokenProvider
    {
        /// <summary>
        /// Получает GitHub токен из зашифрованных настроек
        /// Токен автоматически расшифровывается при получении
        /// </summary>
        public static string? GetToken()
        {
            try
            {
                string settingsFilePath = AppDataHelper.GetSettingsFilePath();
                if (!System.IO.File.Exists(settingsFilePath))
                {
                    return null;
                }
                
                string json = System.IO.File.ReadAllText(settingsFilePath);
                var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(json);
                
                if (settings == null || string.IsNullOrEmpty(settings.GitHubTokenEncrypted))
                {
                    return null;
                }
                
                // Расшифровываем токен
                string decryptedToken = TokenEncryption.Decrypt(settings.GitHubTokenEncrypted);
                
                if (string.IsNullOrWhiteSpace(decryptedToken))
                {
                    MainWindow.Log("[GitHubTokenProvider] Decrypted token is empty - possible decryption error");
                    return null;
                }
                
                // Логируем только первые 10 символов для диагностики (безопасно)
                string tokenPreview = decryptedToken.Length > 10 
                    ? decryptedToken.Substring(0, 10) + "..." 
                    : decryptedToken.Substring(0, Math.Min(decryptedToken.Length, 10));
                MainWindow.Log($"[GitHubTokenProvider] Token decrypted successfully (preview: {tokenPreview}, length: {decryptedToken.Length})");
                
                return decryptedToken;
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[GitHubTokenProvider] Error getting token: {ex.Message}");
                return null;
            }
        }
        
    }
}

