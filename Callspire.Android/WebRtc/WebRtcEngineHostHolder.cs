namespace Softphone.Android.WebRtc
{
    /// <summary>Process-wide WebRTC engine host for the Android head (stub until native engine lands).</summary>
    public static class WebRtcEngineHostHolder
    {
        public static IWebRtcEngineHost? Current { get; set; }
    }
}
