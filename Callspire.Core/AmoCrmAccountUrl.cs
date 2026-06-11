using System;

namespace Softphone
{
    /// <summary>
    /// Единые правила сборки HTTPS-базы аккаунта AmoCRM для UI и API (не должны расходиться).
    /// Короткий поддомен из настроек (например <c>mdkb</c>) → <c>https://mdkb.amocrm.ru</c>.
    /// Полный хост (или URL со схемой) сохраняется как есть — для Kommo / кастомных доменов.
    /// </summary>
    public static class AmoCrmAccountUrl
    {
        /// <returns><c>null</c> если строка пустая после trim.</returns>
        public static string? TryBuildWebBaseUrl(string? subdomainRaw)
        {
            if (string.IsNullOrWhiteSpace(subdomainRaw))
                return null;

            var s = subdomainRaw.Trim();
            if (s.Contains('.', StringComparison.Ordinal))
            {
                var domain = s;
                if (domain.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    domain = domain.Substring("https://".Length);
                else if (domain.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    domain = domain.Substring("http://".Length);

                domain = domain.Trim().TrimEnd('/');
                return string.IsNullOrEmpty(domain) ? null : $"https://{domain}";
            }

            return $"https://{s}.amocrm.ru";
        }

        /// <summary>
        /// OAuth referer from Kommo (e.g. <c>https://mdkb.kommo.com</c>) → web base for API calls.
        /// </summary>
        public static string? TryBuildWebBaseUrlFromReferer(string? refererRaw)
        {
            if (string.IsNullOrWhiteSpace(refererRaw))
                return null;

            var s = refererRaw.Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("https://".Length);
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("http://".Length);

            var host = s.Trim().TrimEnd('/').Split('/')[0].Split('?')[0];
            return string.IsNullOrEmpty(host) ? null : $"https://{host}";
        }
    }
}
