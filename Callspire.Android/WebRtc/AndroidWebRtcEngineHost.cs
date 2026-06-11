using System;
using System.Threading.Tasks;

namespace Softphone.Android.WebRtc
{
    /// <summary>
    /// Headless WebRTC engine stub for Android until a native WebRTC host is integrated.
    /// Allows <see cref="WebRtcService"/> to attach without crashing at startup.
    /// </summary>
    public sealed class AndroidWebRtcEngineHost : IWebRtcEngineHost
    {
        public event Action<string>? EngineEvent;

        public bool IsInitialized => false;

        public Task SendAsync(object command)
        {
            AppLog.Log("[AndroidWebRtcEngineHost] WebRTC not yet available on Android — command ignored");
            return Task.CompletedTask;
        }

        public void NotifyUnavailable()
        {
            EngineEvent?.Invoke("{\"type\":\"engine_unavailable\",\"reason\":\"WebRTC is not yet supported on Android.\"}");
        }
    }
}
