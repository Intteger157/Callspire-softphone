using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.Platform
{
    /// <summary>
    /// Single-instance guard for macOS / Linux (the WPF build has its own <c>SingleInstanceManager</c>).
    /// Uses an exclusive lock file plus a Unix domain socket in the app-data folder so a second
    /// process can forward its click-to-call URL to the running instance and exit.
    ///
    /// Note: when launched from a macOS <c>.app</c> bundle LaunchServices already guarantees one
    /// instance and delivers URLs through <c>application:openURLs:</c>; this class covers direct
    /// binary launches, Linux and the case where the app was started before the bundle was registered.
    /// </summary>
    public sealed class CrossPlatformSingleInstance : IDisposable
    {
        private readonly string _lockPath;
        private readonly string _socketPath;
        private FileStream? _lock;
        private Socket? _listener;
        private CancellationTokenSource? _cts;

        public bool IsPrimary { get; private set; }

        public CrossPlatformSingleInstance()
        {
            string dir = AppDataHelper.GetAppDataPath();
            try { Directory.CreateDirectory(dir); } catch { }
            _lockPath = Path.Combine(dir, "instance.lock");
            // Unix socket paths are limited to ~104 bytes; fall back to /tmp when app data is deep.
            string sock = Path.Combine(dir, "instance.sock");
            _socketPath = Encoding.UTF8.GetByteCount(sock) < 100 ? sock : Path.Combine(Path.GetTempPath(), "callspire-instance.sock");
        }

        /// <summary>Try to become the primary instance. Returns false when another instance holds the lock.</summary>
        public bool TryAcquire()
        {
            try
            {
                _lock = new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                IsPrimary = true;
                return true;
            }
            catch (IOException)
            {
                IsPrimary = false;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                IsPrimary = false;
                return false;
            }
            catch (Exception ex)
            {
                // Unknown failure — do not block startup, just skip single-instance semantics.
                AppLog.Log($"[SingleInstance] lock error: {ex.Message}");
                IsPrimary = true;
                return true;
            }
        }

        /// <summary>Primary instance: listen for forwarded messages (one UTF-8 line per connection).</summary>
        public void StartServer(Action<string> onMessage)
        {
            if (!IsPrimary || _listener != null) return;
            try
            {
                try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
                _listener.Listen(4);
                _cts = new CancellationTokenSource();
                _ = AcceptLoopAsync(onMessage, _cts.Token);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[SingleInstance] server start failed: {ex.Message}");
                try { _listener?.Dispose(); } catch { }
                _listener = null;
            }
        }

        private async Task AcceptLoopAsync(Action<string> onMessage, CancellationToken token)
        {
            var listener = _listener!;
            while (!token.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { AppLog.Log($"[SingleInstance] accept: {ex.Message}"); continue; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (client)
                        {
                            var buf = new byte[4096];
                            var sb = new StringBuilder();
                            int n;
                            while ((n = await client.ReceiveAsync(buf, SocketFlags.None, token).ConfigureAwait(false)) > 0)
                            {
                                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                                if (sb.Length > 16 * 1024) break;
                            }
                            string msg = sb.ToString().Trim();
                            if (msg.Length > 0) onMessage(msg);
                        }
                    }
                    catch (Exception ex) { AppLog.Log($"[SingleInstance] receive: {ex.Message}"); }
                }, token);
            }
        }

        /// <summary>Secondary instance: forward a message (e.g. the protocol URL) to the primary. Returns true on success.</summary>
        public bool Forward(string message, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    s.Connect(new UnixDomainSocketEndPoint(_socketPath));
                    var bytes = Encoding.UTF8.GetBytes(message + "\n");
                    s.Send(bytes);
                    s.Shutdown(SocketShutdown.Send);
                    return true;
                }
                catch
                {
                    Thread.Sleep(150);
                }
            }
            AppLog.Log("[SingleInstance] forward failed: primary not reachable");
            return false;
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Dispose(); } catch { }
            _listener = null;
            if (IsPrimary)
            {
                try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
            }
            try { _lock?.Dispose(); } catch { }
            _lock = null;
        }
    }
}
