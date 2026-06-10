using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Softphone
{
    internal enum KommoInitFailureKind
    {
        Auth,
        Network,
        Other
    }

    internal static class KommoInitHelper
    {
        private static readonly int[] DefaultRetryDelaysMs = { 0, 2000, 5000 };

        public static KommoInitFailureKind ClassifyFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is UnauthorizedAccessException)
                    return KommoInitFailureKind.Auth;

                if (e is HttpRequestException or SocketException or TaskCanceledException or OperationCanceledException)
                    return KommoInitFailureKind.Network;
            }

            return KommoInitFailureKind.Other;
        }

        public static KommoInitFailureKind ClassifyFailureMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return KommoInitFailureKind.Other;

            var m = message.ToLowerInvariant();
            if (m.Contains("unauthorized") || m.Contains("invalid token") || m.Contains("token is invalid")
                || m.Contains("re-authorize") || m.Contains("401"))
                return KommoInitFailureKind.Auth;

            if (m.Contains("connection attempt failed") || m.Contains("failed to respond")
                || m.Contains("timed out") || m.Contains("timeout") || m.Contains("no such host")
                || m.Contains("network is unreachable") || m.Contains("actively refused")
                || m.Contains("502") || m.Contains("503") || m.Contains("504"))
                return KommoInitFailureKind.Network;

            return KommoInitFailureKind.Other;
        }

        public static async Task ExecuteWithNetworkRetryAsync(
            Func<Task> action,
            Action<string>? log = null,
            int maxAttempts = 3,
            int[]? retryDelaysMs = null)
        {
            retryDelaysMs ??= DefaultRetryDelaysMs;
            Exception? last = null;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    int delay = attempt < retryDelaysMs.Length ? retryDelaysMs[attempt] : retryDelaysMs[^1];
                    if (delay > 0)
                        await Task.Delay(delay).ConfigureAwait(false);
                }

                try
                {
                    await action().ConfigureAwait(false);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    var kind = ClassifyFailure(ex);
                    if (kind == KommoInitFailureKind.Auth)
                        throw;

                    if (kind == KommoInitFailureKind.Network && attempt < maxAttempts - 1)
                    {
                        log?.Invoke($"Kommo init network error (attempt {attempt + 1}/{maxAttempts}): {ex.Message}. Retrying...");
                        continue;
                    }

                    throw;
                }
            }

            if (last != null)
                throw last;
        }
    }
}
