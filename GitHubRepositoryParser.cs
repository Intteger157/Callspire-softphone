using System;
using System.Text.RegularExpressions;

namespace Softphone
{
    /// <summary>
    /// Парсер для извлечения owner и name из GitHub repository link
    /// </summary>
    public static class GitHubRepositoryParser
    {
        /// <summary>
        /// Парсит GitHub repository link и извлекает owner и name
        /// </summary>
        /// <param name="repositoryLink">Ссылка на репозиторий (например: https://github.com/username/repo или username/repo)</param>
        /// <returns>Tuple с owner и name, или null если парсинг не удался</returns>
        public static (string owner, string name)? ParseRepositoryLink(string? repositoryLink)
        {
            if (string.IsNullOrWhiteSpace(repositoryLink))
                return null;
            
            try
            {
                // Убираем пробелы
                repositoryLink = repositoryLink.Trim();
                
                // Паттерн для GitHub URL: https://github.com/owner/name или http://github.com/owner/name
                var urlPattern = @"(?:https?://)?(?:www\.)?github\.com/([^/]+)/([^/?#]+)";
                var urlMatch = Regex.Match(repositoryLink, urlPattern, RegexOptions.IgnoreCase);
                
                if (urlMatch.Success && urlMatch.Groups.Count >= 3)
                {
                    string owner = urlMatch.Groups[1].Value.Trim();
                    string name = urlMatch.Groups[2].Value.Trim();
                    
                    // Убираем .git из конца имени, если есть
                    if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name.Substring(0, name.Length - 4);
                    }
                    
                    if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(name))
                    {
                        return (owner, name);
                    }
                }
                
                // Паттерн для формата owner/name
                var simplePattern = @"^([^/]+)/([^/]+)$";
                var simpleMatch = Regex.Match(repositoryLink, simplePattern);
                
                if (simpleMatch.Success && simpleMatch.Groups.Count >= 3)
                {
                    string owner = simpleMatch.Groups[1].Value.Trim();
                    string name = simpleMatch.Groups[2].Value.Trim();
                    
                    // Убираем .git из конца имени, если есть
                    if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        name = name.Substring(0, name.Length - 4);
                    }
                    
                    if (!string.IsNullOrEmpty(owner) && !string.IsNullOrEmpty(name))
                    {
                        return (owner, name);
                    }
                }
            }
            catch (Exception ex)
            {
                MainWindow.Log($"[GitHubRepositoryParser] Error parsing repository link: {ex.Message}");
            }
            
            return null;
        }
        
        /// <summary>
        /// Проверяет, является ли строка валидной ссылкой на GitHub репозиторий
        /// </summary>
        public static bool IsValidRepositoryLink(string? repositoryLink)
        {
            return ParseRepositoryLink(repositoryLink) != null;
        }
    }
}

