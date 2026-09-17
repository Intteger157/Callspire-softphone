using System;
using System.Collections.Generic;

namespace Softphone.Platform
{
    /// <summary>
    /// Process-wide funnel for click-to-call URLs (<c>callspire://…</c>, <c>tel:</c>) that may arrive
    /// before the controller exists: command-line args, a forwarded message from a second instance,
    /// or a macOS <c>application:openURLs:</c> activation. URLs are queued until a handler is attached.
    /// </summary>
    public static class ProtocolActivation
    {
        private static readonly object Gate = new();
        private static readonly Queue<string> Pending = new();
        private static Action<string>? _handler;

        public static bool LooksLikeProtocolUrl(string? s) =>
            !string.IsNullOrWhiteSpace(s) &&
            (s.StartsWith("callspire:", StringComparison.OrdinalIgnoreCase) ||
             s.StartsWith("tel:", StringComparison.OrdinalIgnoreCase));

        /// <summary>Returns the first protocol URL among command-line args, if any.</summary>
        public static string? FindInArgs(string[]? args)
        {
            if (args == null) return null;
            foreach (var a in args)
                if (LooksLikeProtocolUrl(a)) return a.Trim();
            return null;
        }

        public static void Enqueue(string url)
        {
            if (!LooksLikeProtocolUrl(url)) return;
            Action<string>? h;
            lock (Gate)
            {
                h = _handler;
                if (h == null) { Pending.Enqueue(url); return; }
            }
            Safe(h, url);
        }

        /// <summary>Attach the consumer (controller) and flush anything queued so far.</summary>
        public static void AttachHandler(Action<string> handler)
        {
            List<string> flush;
            lock (Gate)
            {
                _handler = handler;
                flush = new List<string>(Pending);
                Pending.Clear();
            }
            foreach (var u in flush) Safe(handler, u);
        }

        private static void Safe(Action<string> h, string url)
        {
            try { h(url); }
            catch (Exception ex) { AppLog.Log($"[ProtocolActivation] handler error: {ex.Message}"); }
        }
    }
}
