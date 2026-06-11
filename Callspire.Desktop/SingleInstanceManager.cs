#if WINDOWS
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Softphone
{
    /// <summary>
    /// Single-instance: one Callspire process. Secondary launches forward callspire:// to the primary via pipe.
    /// </summary>
    public static class SingleInstanceManager
    {
        private const string MutexName = "CallspireSoftphone_SingleInstance_Mutex";
        private const string PipeName = "CallspireSoftphone_SingleInstance_Pipe";
        private const int MaxPipeServerInstances = 10;
        private static readonly TimeSpan DefaultForwardTimeout = TimeSpan.FromSeconds(45);

        private static Mutex? _mutex;
        private static bool _isFirstInstance;
        private static MainWindow? _registeredMainWindow;
        private static readonly ConcurrentQueue<string> _pendingMessages = new();

        public static bool IsFirstInstance()
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out _isFirstInstance);
                return _isFirstInstance;
            }
            catch
            {
                _isFirstInstance = false;
                return false;
            }
        }

        /// <summary>True when another process already holds the app mutex.</summary>
        public static bool AnotherInstanceIsRunning()
        {
            if (_isFirstInstance)
                return false;

            try
            {
                using var probe = Mutex.OpenExisting(MutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch
            {
                return !_isFirstInstance;
            }
        }

        public static void RegisterMainWindow(MainWindow mainWindow)
        {
            _registeredMainWindow = mainWindow;
            while (_pendingMessages.TryDequeue(out var queued))
                DeliverMessage(queued);
        }

        public static void UnregisterMainWindow(MainWindow mainWindow)
        {
            if (ReferenceEquals(_registeredMainWindow, mainWindow))
                _registeredMainWindow = null;
        }

        /// <summary>
        /// Start pipe server immediately on the primary instance (before MainWindow exists).
        /// </summary>
        public static void StartServer()
        {
            if (!_isFirstInstance)
                return;

            Task.Run(ServerLoop);
        }

        /// <summary>
        /// Forward a protocol URL to the running instance. Blocks up to <paramref name="timeout"/>.
        /// Secondary instances with a callspire:// URL must call this and then exit — never start UI.
        /// </summary>
        public static bool WaitAndForwardToExistingInstance(string message, TimeSpan? timeout = null)
        {
            var limit = timeout ?? DefaultForwardTimeout;
            var deadline = DateTime.UtcNow + limit;
            var attempt = 0;

            Log($"[SingleInstance] Forwarding protocol message to primary instance (timeout={limit.TotalSeconds:0}s)...");

            while (DateTime.UtcNow < deadline)
            {
                var remainingMs = (int)Math.Max(250, (deadline - DateTime.UtcNow).TotalMilliseconds);
                var connectMs = Math.Min(2000, remainingMs);

                if (TrySendOnce(message, connectMs))
                {
                    Log("[SingleInstance] Protocol message forwarded successfully");
                    return true;
                }

                Thread.Sleep(Math.Min(400, 150 + attempt * 50));
                attempt++;
            }

            Log("[SingleInstance] Failed to forward protocol message — primary instance did not accept pipe connection in time");
            return false;
        }

        public static bool SendMessageToExistingInstance(string message)
            => WaitAndForwardToExistingInstance(message, TimeSpan.FromSeconds(8));

        private static void ServerLoop()
        {
            try
            {
                while (_isFirstInstance)
                {
                    try
                    {
                        using var pipeServer = new NamedPipeServerStream(
                            PipeName,
                            PipeDirection.In,
                            MaxPipeServerInstances,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        pipeServer.WaitForConnection();

                        using var reader = new StreamReader(pipeServer);
                        string message = reader.ReadToEnd();
                        if (!string.IsNullOrEmpty(message))
                            DeliverMessage(message);
                    }
                    catch (Exception ex)
                    {
                        if (_isFirstInstance)
                            Log($"[SingleInstance] Pipe server error: {ex.Message}");
                    }
                }
            }
            catch
            {
                // shutdown
            }
        }

        private static bool TrySendOnce(string message, int connectTimeoutMs)
        {
            try
            {
                using var pipeClient = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                pipeClient.Connect(Math.Max(250, connectTimeoutMs));

                using var writer = new StreamWriter(pipeClient) { AutoFlush = true };
                writer.Write(message);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SingleInstanceManager] Send attempt failed: {ex.Message}");
                return false;
            }
        }

        private static void DeliverMessage(string message)
        {
            try
            {
                Log($"[SingleInstance] Received forwarded message: {LogSanitizer.RedactUrlWithToken(message)}");

                void Dispatch()
                {
                    var mainWindow = _registeredMainWindow ?? Application.Current?.MainWindow as MainWindow;
                    if (mainWindow == null)
                    {
                        _pendingMessages.Enqueue(message);
                        Log("[SingleInstance] MainWindow not ready — message queued");
                        return;
                    }

                    WindowForegroundHelper.RequestUserAttention(mainWindow);

                    if (message.StartsWith("callspire://", StringComparison.OrdinalIgnoreCase))
                        mainWindow.HandleProtocolMessage(message);
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                    dispatcher.BeginInvoke(new Action(Dispatch));
                else
                    Dispatch();
            }
            catch (Exception ex)
            {
                Log($"[SingleInstance] Error delivering message: {ex.Message}");
            }
        }

        public static void Cleanup()
        {
            try
            {
                _isFirstInstance = false;
                _registeredMainWindow = null;
                while (_pendingMessages.TryDequeue(out _)) { }
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
                _mutex = null;
            }
            catch
            {
                // ignore
            }
        }

        private static void Log(string message)
        {
            Debug.WriteLine(message);
            try { FileLogService.Instance.Enqueue(message); } catch { }
        }
    }
}

#endif // WINDOWS
