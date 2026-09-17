using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.WebRtc
{
    /// <summary>
    /// Serves the <c>WebRtcClient/</c> folder (index.html, slot.html, phone.js, jssip.min.js, …) over
    /// <c>http://127.0.0.1:{port}/</c> for the Avalonia <c>NativeWebView</c> host.
    ///
    /// Windows/WebView2 maps a virtual host (<c>https://softphone.local</c>) to the folder; WKWebView has
    /// no equivalent and <c>file://</c> pages cannot use <c>getUserMedia</c> reliably. Loopback HTTP is a
    /// "potentially trustworthy" origin per the Secure Contexts spec, so WebRTC / getUserMedia work.
    /// Only loopback binds are used; the listener never accepts remote connections.
    /// </summary>
    public sealed class WebRtcAssetServer : IDisposable
    {
        private readonly string _root;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        public int Port { get; private set; }
        public string BaseUrl => $"http://127.0.0.1:{Port}/";
        public bool IsRunning => _listener?.IsListening == true;

        public WebRtcAssetServer(string rootDirectory)
        {
            _root = Path.GetFullPath(rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory)));
        }

        /// <summary>Locates <c>WebRtcClient</c> next to the executable (handles single-file extraction dirs).</summary>
        public static string? LocateClientDirectory()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "WebRtcClient"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WebRtcClient"),
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "WebRtcClient"),
                // macOS .app bundle: Contents/MacOS/Callspire → Contents/Resources/WebRtcClient
                Path.Combine(Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "", "..", "Resources", "WebRtcClient"),
            };
            foreach (var c in candidates)
            {
                try
                {
                    var full = Path.GetFullPath(c);
                    if (Directory.Exists(full) && File.Exists(Path.Combine(full, "index.html"))) return full;
                }
                catch { }
            }
            return null;
        }

        public void Start()
        {
            if (_listener != null) return;
            if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);

            // Try a handful of ports: pick a free one, then bind HttpListener to it.
            Exception? last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int port = GetFreeLoopbackPort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { listener.Close(); } catch { }
                }
            }
            if (_listener == null) throw new InvalidOperationException("Could not start WebRTC asset server", last);

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            AppLog.Log($"[WebRtcAssetServer] serving {_root} at {BaseUrl}");
        }

        private static int GetFreeLoopbackPort()
        {
            using var sock = new TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            int port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            var listener = _listener!;
            while (!token.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { if (token.IsCancellationRequested) break; continue; }
                catch (Exception ex) { AppLog.Log($"[WebRtcAssetServer] accept: {ex.Message}"); continue; }

                _ = Task.Run(() => Serve(ctx), token);
            }
        }

        private void Serve(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            try
            {
                // Loopback only — refuse anything else defensively.
                if (!IPAddress.IsLoopback(ctx.Request.RemoteEndPoint?.Address ?? IPAddress.None))
                {
                    res.StatusCode = 403; res.Close(); return;
                }

                string rel = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/").TrimStart('/');
                if (rel.Length == 0) rel = "index.html";
                rel = rel.Replace('/', Path.DirectorySeparatorChar);

                string path = Path.GetFullPath(Path.Combine(_root, rel));
                if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                {
                    res.StatusCode = 404; res.Close(); return;
                }

                res.ContentType = ContentTypeFor(path);
                res.Headers["Cache-Control"] = "no-cache";
                // Harmless for loopback; lets slot iframes and fetch() of local assets work in strict engines.
                res.Headers["Access-Control-Allow-Origin"] = "*";

                byte[] bytes = File.ReadAllBytes(path);
                res.ContentLength64 = bytes.Length;
                res.OutputStream.Write(bytes, 0, bytes.Length);
                res.Close();
            }
            catch (Exception ex)
            {
                AppLog.Log($"[WebRtcAssetServer] serve error: {ex.Message}");
                try { res.StatusCode = 500; res.Close(); } catch { }
            }
        }

        private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json; charset=utf-8",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".png" => "image/png",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream",
        };

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            try { _loop?.Wait(500); } catch { }
            _cts?.Dispose();
            _cts = null;
        }
    }
}
