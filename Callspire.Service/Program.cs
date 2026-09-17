using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Softphone.Platform;

namespace Softphone.Service
{
    /// <summary>
    /// Entry point of the macOS telephony sidecar.
    ///
    /// <code>
    ///   Callspire.Service [--socket PATH] [--parent-pid PID] [--headless]
    /// </code>
    /// Default socket: <c>~/Library/Application Support/Callspire/service.sock</c> (see <see cref="DefaultSocketPath"/>).
    /// When <c>--parent-pid</c> is given the process exits as soon as the Swift app is gone.
    /// <c>--headless</c> starts telephony immediately instead of waiting for the first UI connection
    /// (useful for smoke tests without the Swift shell).
    /// </summary>
    internal static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            string socketPath = DefaultSocketPath();
            int parentPid = 0;
            bool headless = false;
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--socket" when i + 1 < args.Length: socketPath = args[++i]; break;
                    case "--parent-pid" when i + 1 < args.Length: int.TryParse(args[++i], out parentPid); break;
                    case "--headless": headless = true; break;
                }
            }

            using var dispatcher = new ServiceDispatcher();
            ServiceBootstrap.Initialize(dispatcher);
            AppLog.Log($"[Service] starting v{UpdateService.GetCurrentVersion()} pid={Environment.ProcessId} socket={socketPath}");

            // The Swift app owns single-instance semantics for the bundle; this guard only prevents two
            // sidecars fighting over the same socket when launched by hand.
            using var instance = new CrossPlatformSingleInstance();
            if (!instance.TryAcquire())
            {
                AppLog.Log("[Service] another sidecar instance is already running — exiting");
                return 2;
            }

            var host = new ServiceHost(socketPath, dispatcher);
            var stop = new CancellationTokenSource();
            void RequestStop() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; RequestStop(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestStop();
            PosixSignals.OnTerminate(RequestStop);

            try
            {
                host.Start();
                if (headless) await host.EnsureStartedAsync().ConfigureAwait(false);

                var parentWatch = parentPid > 0 ? WatchParentAsync(parentPid, stop.Token) : Task.Delay(Timeout.Infinite, stop.Token);
                var finished = await Task.WhenAny(host.ShutdownRequested, parentWatch, Task.Delay(Timeout.Infinite, stop.Token)).ConfigureAwait(false);
                AppLog.Log(finished == host.ShutdownRequested ? "[Service] shutdown requested by UI"
                    : finished == parentWatch ? "[Service] parent process exited — shutting down"
                    : "[Service] termination signal");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLog.Log($"[Service] fatal: {ex}");
                return 1;
            }
            finally
            {
                try { await host.DisposeAsync().ConfigureAwait(false); } catch { }
                AppLog.Log("[Service] stopped");
            }
            return 0;
        }

        public static string DefaultSocketPath()
        {
            string dir = AppDataHelper.GetAppDataPath();
            string path = Path.Combine(dir, "service.sock");
            // sun_path is limited to ~104 bytes on macOS.
            return Encoding.UTF8.GetByteCount(path) < 100 ? path : Path.Combine(Path.GetTempPath(), "callspire-service.sock");
        }

        private static async Task WatchParentAsync(int pid, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById(pid);
                    if (p.HasExited) return;
                }
                catch (ArgumentException) { return; }
                catch { }
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
        }
    }

    internal static class PosixSignals
    {
        public static void OnTerminate(Action action)
        {
            if (OperatingSystem.IsWindows()) return;
            try
            {
                System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; action(); });
                System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; action(); });
            }
            catch { }
        }
    }
}
