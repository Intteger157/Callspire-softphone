using System.Text.RegularExpressions;

namespace Softphone
{
    /// <summary>
    /// Masks secrets in strings before writing to user-facing logs (e.g. JWT in <c>callspire://cdr-auth?token=</c>).
    /// </summary>
    internal static class LogSanitizer
    {
        private static readonly Regex TokenQueryParam = new(
            @"([?&]token=)([^&]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string RedactUrlWithToken(string? url)
        {
            if (string.IsNullOrEmpty(url)) return url ?? "";
            return TokenQueryParam.Replace(url, m => m.Groups[1].Value + "***REDACTED***");
        }
    }
}
