using System;
using System.Globalization;

namespace Softphone
{
    /// <summary>
    /// Parses stored SIP server strings (<c>host:port</c>) without breaking on colons in URLs or IPv6.
    /// Naive <c>Split(':')</c> turns <c>http://pbx:5060</c> into host <c>http</c> and corrupts settings.
    /// </summary>
    internal static class SipEndpointHelper
    {
        public const int DefaultSipPort = 5060;

        /// <summary>
        /// Split <see cref="AppSettings.SipServer"/> / <see cref="AppSettings.SipServer2"/> into host and port.
        /// </summary>
        public static void ParseStoredSipServer(string? stored, out string host, out int port)
        {
            host = "";
            port = DefaultSipPort;
            if (string.IsNullOrWhiteSpace(stored))
                return;

            stored = stored.Trim();

            // [::1]:5060
            if (stored.Length > 0 && stored[0] == '[')
            {
                int close = stored.IndexOf(']', StringComparison.Ordinal);
                if (close > 1 && close + 1 < stored.Length && stored[close + 1] == ':')
                {
                    host = stored.Substring(1, close - 1);
                    if (int.TryParse(stored.AsSpan(close + 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p)
                        && p is >= 1 and <= 65535)
                        port = p;
                    return;
                }
            }

            // http(s)://host:port — must run before "last colon" rule
            if (stored.Contains("://", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(stored, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
                {
                    host = uri.IdnHost;
                    port = MapUriPortToSip(uri);
                    return;
                }
            }

            // sip:user@host:port or sip:host (no //)
            if (stored.StartsWith("sip:", StringComparison.OrdinalIgnoreCase)
                || stored.StartsWith("sips:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = stored.AsSpan();
                rest = rest.StartsWith("sips:", StringComparison.OrdinalIgnoreCase) ? rest.Slice(5) : rest.Slice(4);
                var restStr = rest.ToString();
                int at = restStr.LastIndexOf('@');
                if (at >= 0)
                    restStr = restStr[(at + 1)..];
                ParseStoredSipServer(restStr, out host, out port);
                return;
            }

            // Plain host:port — only split on last ':' if the suffix is a valid port number
            int lc = stored.LastIndexOf(':');
            if (lc > 0)
            {
                ReadOnlySpan<char> tail = stored.AsSpan(lc + 1);
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p3)
                    && p3 is >= 1 and <= 65535)
                {
                    host = stored[..lc];
                    port = p3;
                    return;
                }
            }

            host = stored;
        }

        public static string GetHostOnly(string? stored)
        {
            ParseStoredSipServer(stored, out string host, out _);
            return host;
        }

        /// <summary>
        /// Strips scheme/host from a URL pasted into the server field; optionally returns port from the URL.
        /// </summary>
        public static string NormalizeServerInput(string? serverInput, out int? suggestedPort)
        {
            suggestedPort = null;
            if (string.IsNullOrWhiteSpace(serverInput))
                return "";

            string s = serverInput.Trim();
            if (!s.Contains("://", StringComparison.Ordinal))
                return s;

            if (!Uri.TryCreate(s, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
                return s;

            suggestedPort = MapUriPortToSip(uri);
            return uri.IdnHost;
        }

        /// <summary>HTTP(S) default ports are not SIP ports; treat as <see cref="DefaultSipPort"/>.</summary>
        private static int MapUriPortToSip(Uri uri)
        {
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.Port == 80)
                return DefaultSipPort;
            if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) && uri.Port == 443)
                return DefaultSipPort;
            return uri.Port > 0 ? uri.Port : DefaultSipPort;
        }

        /// <summary>
        /// Collapses accidental double slashes in the path (e.g. <c>wss://host//webrtc</c>).
        /// </summary>
        public static string NormalizeWebRtcWsUri(string? ws)
        {
            if (string.IsNullOrWhiteSpace(ws))
                return ws?.Trim() ?? "";

            string trimmed = ws.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
                return trimmed;

            var b = new UriBuilder(uri);
            string path = b.Path ?? "";
            while (path.StartsWith("//", StringComparison.Ordinal))
                path = path[1..];
            if (path.Length == 0)
                path = "/";
            else if (path[0] != '/')
                path = "/" + path;
            b.Path = path;
            return b.Uri.ToString();
        }
    }
}
