namespace Softphone
{
    /// <summary>Process-wide WebRTC engine host for macOS/Linux bootstrap paths.</summary>
    public static class WebRtcEngineHostPlatform
    {
        public static IWebRtcEngineHost? Current { get; set; }
    }
}
