namespace Softphone
{
    public class AppSettings
    {
        public string? SipServer { get; set; }
        public string? SipUsername { get; set; }
        public string? SipPassword { get; set; }
        
        // Audio device settings
        public string? MicrophoneDeviceGuid { get; set; }
        public string? SpeakerDeviceGuid { get; set; }
        public int? MicrophoneDeviceNumber { get; set; }
        public int? SpeakerDeviceNumber { get; set; }
    }
}

