using System;
using System.Threading;
using System.Threading.Tasks;

namespace Softphone.AppHost
{
    /// <summary>
    /// Sleep / wake recovery, ported from WPF <c>MainWindow.SystemEvents_PowerModeChanged</c> /
    /// <c>HandleResumeAsync</c> / <c>RecoverWebRtcEngineAsync</c>. The platform shell reports the
    /// events (Windows: SystemEvents; macOS: NSWorkspace will/didWake via the Swift app → IPC).
    /// </summary>
    public sealed partial class DesktopAppController
    {
        private int _resumeRecoveryRunning;

        /// <summary>System is about to sleep: silence background loops that would otherwise spam reconnects.</summary>
        public void HandleSystemSleep()
        {
            Log("[Controller] System sleep");
            foreach (var slot in new[] { "main", "secondary" })
            {
                try
                {
                    var svc = WebRtcService.GetSlot(slot);
                    svc.StopWatchdog();
                    svc.StopAutoReconnect();
                }
                catch { }
            }
        }

        /// <summary>
        /// System woke up (or the network came back / the web engine process died): recreate the WebRTC
        /// engine host through <see cref="WebRtcHostFactory"/> when a WebRTC line is configured and
        /// re-register every line from current settings.
        /// </summary>
        public async Task HandleSystemResumeAsync(string reason)
        {
            if (Interlocked.Exchange(ref _resumeRecoveryRunning, 1) == 1) return;
            try
            {
                Log($"[Controller] Resume recovery: starting (reason={reason})");
                await Task.Delay(600).ConfigureAwait(false); // let the network stack settle (mirrors WPF)
                if (_disposed) return;

                var settings = LoadSettingsWithMigrations();
                _settings = settings;

                bool anyWebRtc = MainUsesWebRtc(settings) || AppSettings.SecondaryLineUsesWebRtc(settings);
                if (anyWebRtc)
                {
                    StopWebRtcSlot("main");
                    StopWebRtcSlot("secondary");
                    try { (_webRtcHost as IDisposable)?.Dispose(); } catch { }
                    _webRtcHost = null; // EnsureWebRtcHostAsync will ask the shell for a fresh host
                }

                await ConnectAllAsync(settings).ConfigureAwait(false);
                Log("[Controller] Resume recovery: done");
            }
            catch (Exception ex)
            {
                Log($"[Controller] Resume recovery error: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _resumeRecoveryRunning, 0);
            }
        }
    }
}
