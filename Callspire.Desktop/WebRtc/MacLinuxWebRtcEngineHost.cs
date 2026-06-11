using System;
using System.Threading.Tasks;

namespace Softphone
{
    /// <summary>
    /// Headless no-op IWebRtcEngineHost for macOS and Linux.
    ///
    /// WebRTC calls on non-Windows platforms are not yet supported (Phase 7).
    /// This stub lets WebRtcService initialize without crashing so that:
    ///   • SIP-only calls work normally on all platforms.
    ///   • The binary boots and the UI shows on macOS / Linux.
    ///
    /// When a real non-Windows WebRTC host is available (e.g. a native Janus/Chime
    /// SDK wrapper), replace this class with a proper implementation.
    /// </summary>
    public sealed class MacLinuxWebRtcEngineHost : IWebRtcEngineHost
    {
        public event Action<string>? EngineEvent;

        public bool IsInitialized => false;

        public Task SendAsync(object command)
        {
            AppLog.Log("[MacLinuxWebRtcEngineHost] SendAsync called but WebRTC engine is not available on this platform. Command ignored.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Raises a synthetic "unavailable" event so that any pending WebRTC
        /// call state machines can handle the absence of the engine gracefully.
        /// </summary>
        public void NotifyUnavailable()
        {
            EngineEvent?.Invoke("{\"type\":\"engine_unavailable\",\"reason\":\"WebRTC is not supported on this platform yet.\"}");
        }
    }
}
