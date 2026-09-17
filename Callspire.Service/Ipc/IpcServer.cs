using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.Service.Ipc
{
    /// <summary>Handles one inbound request; returns the result object (serialized as <c>result</c>).</summary>
    public delegate Task<object?> IpcRequestHandler(JsonElement? @params, CancellationToken ct);

    /// <summary>
    /// NDJSON-over-Unix-socket server. Exactly one UI client (the Swift app) is served at a time; a
    /// newer connection replaces the previous one (e.g. after the Swift process restarted).
    /// Thread-safe: <see cref="SendEventAsync"/> / <see cref="RequestAsync"/> may be called from any thread.
    /// </summary>
    public sealed class IpcServer : IAsyncDisposable
    {
        private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(120);

        private readonly string _socketPath;
        private readonly Dictionary<string, IpcRequestHandler> _handlers = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement?>> _pending = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private Socket? _listener;
        private Task? _acceptLoop;
        private volatile Connection? _client;
        private long _nextId;

        public event Action? ClientConnected;
        public event Action? ClientDisconnected;

        public bool HasClient => _client?.IsOpen == true;

        public IpcServer(string socketPath)
        {
            _socketPath = socketPath ?? throw new ArgumentNullException(nameof(socketPath));
        }

        public void Register(string method, IpcRequestHandler handler)
        {
            lock (_handlers) _handlers[method] = handler;
        }

        /// <summary>Convenience for synchronous handlers.</summary>
        public void Register(string method, Func<JsonElement?, object?> handler)
            => Register(method, (p, _) => Task.FromResult(handler(p)));

        public void Start()
        {
            if (_listener != null) return;
            try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
            Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            _listener.Listen(2);
            if (!OperatingSystem.IsWindows())
            {
                try { File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            AppLog.Log($"[IPC] listening on {_socketPath}");
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            var listener = _listener!;
            while (!token.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await listener.AcceptAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex) { AppLog.Log($"[IPC] accept: {ex.Message}"); await Task.Delay(200, token).ConfigureAwait(false); continue; }

                var previous = _client;
                var conn = new Connection(socket);
                _client = conn;
                if (previous != null)
                {
                    AppLog.Log("[IPC] replacing previous UI client");
                    previous.Close();
                }
                AppLog.Log("[IPC] UI client connected");
                try { ClientConnected?.Invoke(); } catch (Exception ex) { AppLog.Log($"[IPC] ClientConnected handler: {ex.Message}"); }
                _ = Task.Run(() => ReadLoopAsync(conn, token), token);
            }
        }

        private async Task ReadLoopAsync(Connection conn, CancellationToken token)
        {
            try
            {
                using var reader = new StreamReader(conn.Stream, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
                while (!token.IsCancellationRequested && conn.IsOpen)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(token).ConfigureAwait(false); }
                    catch (IOException) { break; }
                    catch (ObjectDisposedException) { break; }
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    _ = Task.Run(() => HandleLineAsync(conn, line, token), token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Log($"[IPC] read loop: {ex.Message}"); }
            finally
            {
                conn.Close();
                if (ReferenceEquals(_client, conn))
                {
                    _client = null;
                    AppLog.Log("[IPC] UI client disconnected");
                    FailPending("UI client disconnected");
                    try { ClientDisconnected?.Invoke(); } catch (Exception ex) { AppLog.Log($"[IPC] ClientDisconnected handler: {ex.Message}"); }
                }
            }
        }

        private async Task HandleLineAsync(Connection conn, string line, CancellationToken token)
        {
            IpcMessage? msg;
            try { msg = JsonSerializer.Deserialize<IpcMessage>(line, IpcJson.Options); }
            catch (Exception ex)
            {
                AppLog.Log($"[IPC] bad json: {ex.Message} :: {Truncate(line, 200)}");
                return;
            }
            if (msg == null) return;

            switch (msg.Type)
            {
                case IpcMessage.TypeRequest:
                    await HandleRequestAsync(conn, msg, token).ConfigureAwait(false);
                    break;
                case IpcMessage.TypeResponse:
                    if (msg.Id != null && _pending.TryRemove(msg.Id, out var tcs))
                    {
                        if (msg.Error != null) tcs.TrySetException(new IpcException(msg.Error.Message, msg.Error.Code));
                        else tcs.TrySetResult(msg.Result);
                    }
                    break;
                case IpcMessage.TypeEvent:
                    // Swift → C# notifications are modelled as requests without a response expectation.
                    if (msg.Event != null)
                        await HandleRequestAsync(conn, new IpcMessage { Type = IpcMessage.TypeRequest, Method = msg.Event, Params = msg.Data }, token).ConfigureAwait(false);
                    break;
                default:
                    AppLog.Log($"[IPC] unknown message type '{msg.Type}'");
                    break;
            }
        }

        private async Task HandleRequestAsync(Connection conn, IpcMessage msg, CancellationToken token)
        {
            IpcRequestHandler? handler;
            lock (_handlers) _handlers.TryGetValue(msg.Method ?? "", out handler);

            if (handler == null)
            {
                AppLog.Log($"[IPC] unknown method '{msg.Method}'");
                if (msg.Id != null) await WriteAsync(conn, IpcMessage.ErrorResponse(msg.Id, $"Unknown method '{msg.Method}'", "unknown_method")).ConfigureAwait(false);
                return;
            }

            try
            {
                var result = await handler(msg.Params, token).ConfigureAwait(false);
                if (msg.Id != null) await WriteAsync(conn, IpcMessage.Response(msg.Id, result)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[IPC] {msg.Method} failed: {ex.Message}");
                if (msg.Id != null) await WriteAsync(conn, IpcMessage.ErrorResponse(msg.Id, ex.Message, (ex as IpcException)?.Code)).ConfigureAwait(false);
            }
        }

        // ───────────────────────── outbound ─────────────────────────

        public Task SendEventAsync(string name, object? data = null)
        {
            var conn = _client;
            if (conn == null || !conn.IsOpen) return Task.CompletedTask;
            return WriteAsync(conn, IpcMessage.EventMessage(name, data));
        }

        /// <summary>Send a request to the UI and await its response (modal prompts, WebRTC bridge).</summary>
        public async Task<JsonElement?> RequestAsync(string method, object? @params, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var conn = _client;
            if (conn == null || !conn.IsOpen) throw new IpcException("UI client is not connected", "no_client");

            string id = "s" + Interlocked.Increment(ref _nextId).ToString();
            var tcs = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;

            try
            {
                await WriteAsync(conn, IpcMessage.Request(id, method, @params)).ConfigureAwait(false);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
                timeoutCts.CancelAfter(timeout ?? DefaultRequestTimeout);
                await using var reg = timeoutCts.Token.Register(() => tcs.TrySetException(new IpcException($"UI did not answer '{method}' in time", "timeout")));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public async Task<T?> RequestAsync<T>(string method, object? @params, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var el = await RequestAsync(method, @params, timeout, ct).ConfigureAwait(false);
            if (el == null || el.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return default;
            return IpcJson.Deserialize<T>(el.Value);
        }

        private async Task WriteAsync(Connection conn, IpcMessage message)
        {
            byte[] bytes;
            try { bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, IpcJson.EnvelopeOptions) + "\n"); }
            catch (Exception ex) { AppLog.Log($"[IPC] serialize {message.Method ?? message.Event}: {ex.Message}"); return; }

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!conn.IsOpen) return;
                await conn.Stream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                await conn.Stream.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Log($"[IPC] write failed: {ex.Message}");
                conn.Close();
            }
            finally { _writeLock.Release(); }
        }

        private void FailPending(string reason)
        {
            foreach (var kv in _pending)
                if (_pending.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(new IpcException(reason, "disconnected"));
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener?.Dispose(); } catch { }
            _client?.Close();
            FailPending("service shutting down");
            if (_acceptLoop != null) { try { await _acceptLoop.ConfigureAwait(false); } catch { } }
            try { if (File.Exists(_socketPath)) File.Delete(_socketPath); } catch { }
        }

        private sealed class Connection
        {
            private readonly Socket _socket;
            private int _closed;
            public NetworkStream Stream { get; }
            public bool IsOpen => _closed == 0 && _socket.Connected;

            public Connection(Socket socket)
            {
                _socket = socket;
                Stream = new NetworkStream(socket, ownsSocket: true);
            }

            public void Close()
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0) return;
                try { _socket.Shutdown(SocketShutdown.Both); } catch { }
                try { Stream.Dispose(); } catch { }
            }
        }
    }
}
