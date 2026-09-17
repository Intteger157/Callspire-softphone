using System;

namespace Softphone
{
    /// <summary>Parse and compare TURN URIs for settings UI and gateway sync.</summary>
    public static class TurnUriHelper
    {
        public sealed class TurnLikeParsed
        {
            public string Scheme { get; init; } = "";
            public string Host { get; init; } = "";
            public int? Port { get; init; }
            public string? Transport { get; init; }
        }

        public static TurnLikeParsed ParseTurnLike(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return new TurnLikeParsed();

            string raw = input.Trim();
            string scheme = "";
            if (raw.StartsWith("turns:", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "turns";
                raw = raw.Substring("turns:".Length);
            }
            else if (raw.StartsWith("turn:", StringComparison.OrdinalIgnoreCase))
            {
                scheme = "turn";
                raw = raw.Substring("turn:".Length);
            }

            raw = raw.TrimStart('/');

            string query = "";
            int qIdx = raw.IndexOf('?', StringComparison.Ordinal);
            if (qIdx >= 0)
            {
                query = raw.Substring(qIdx + 1);
                raw = raw.Substring(0, qIdx);
            }

            int slashIdx = raw.IndexOf('/', StringComparison.Ordinal);
            if (slashIdx >= 0) raw = raw.Substring(0, slashIdx);

            int atIdx = raw.LastIndexOf("@", StringComparison.Ordinal);
            if (atIdx >= 0) raw = raw.Substring(atIdx + 1);

            raw = raw.Trim();

            string? transport = null;
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("transport", StringComparison.OrdinalIgnoreCase))
                {
                    var v = kv[1].Trim().ToLowerInvariant();
                    if (v == "udp" || v == "tcp") transport = v;
                    break;
                }
            }

            string host = raw;
            int? port = null;
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                int close = host.IndexOf(']', StringComparison.Ordinal);
                if (close > 0)
                {
                    string inside = host.Substring(1, close - 1);
                    string rest = host.Substring(close + 1);
                    host = inside;
                    if (rest.StartsWith(":", StringComparison.Ordinal) &&
                        int.TryParse(rest.Substring(1), out var p))
                        port = p;
                }
            }
            else
            {
                int colon = host.LastIndexOf(":", StringComparison.Ordinal);
                if (colon > 0 && colon < host.Length - 1 && int.TryParse(host.Substring(colon + 1), out var p))
                {
                    port = p;
                    host = host.Substring(0, colon);
                }
            }

            return new TurnLikeParsed
            {
                Scheme = scheme,
                Host = host.Trim(),
                Port = port,
                Transport = transport
            };
        }

        /// <summary>Canonical key for equality: scheme/host/port/transport (lowercase host).</summary>
        public static string NormalizeForCompare(string? turnUri)
        {
            if (string.IsNullOrWhiteSpace(turnUri))
                return "";

            var parsed = ParseTurnLike(turnUri);
            if (string.IsNullOrWhiteSpace(parsed.Host))
                return turnUri.Trim().ToLowerInvariant();

            string scheme = string.IsNullOrWhiteSpace(parsed.Scheme) ? "turn" : parsed.Scheme.ToLowerInvariant();
            int port = parsed.Port ?? (scheme == "turns" ? 5349 : 3478);
            string transport = parsed.Transport ?? (scheme == "turns" ? "tcp" : "udp");
            return $"{scheme}:{parsed.Host.ToLowerInvariant()}:{port}?transport={transport}";
        }

        public static bool AreTurnSettingsEqual(
            string? localUri,
            string? localUsername,
            string? localPassword,
            string? remoteUri,
            string? remoteUsername,
            string? remotePassword)
        {
            if (string.IsNullOrWhiteSpace(localUri) && string.IsNullOrWhiteSpace(remoteUri))
                return true;
            if (string.IsNullOrWhiteSpace(localUri) || string.IsNullOrWhiteSpace(remoteUri))
                return false;

            if (!string.Equals(
                    NormalizeForCompare(localUri),
                    NormalizeForCompare(remoteUri),
                    StringComparison.Ordinal))
                return false;

            string lu = (localUsername ?? "").Trim();
            string ru = (remoteUsername ?? "").Trim();
            if (!string.Equals(lu, ru, StringComparison.Ordinal))
                return false;

            string lp = localPassword ?? "";
            string rp = remotePassword ?? "";
            return string.Equals(lp, rp, StringComparison.Ordinal);
        }
    }
}
