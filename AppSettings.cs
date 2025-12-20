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
        
        // Audio codec settings
        public string AudioCodec { get; set; } = "PCMU"; // По умолчанию G.711 μ-law (PCMU)
        public int AudioSampleRate { get; set; } = 16000; // Частота дискретизации (16000 для лучшего качества)
        public int AudioBitrate { get; set; } = 64000; // Битрейт для Opus
        
        // WebRTC settings
        public bool UseWebRtcAudio { get; set; } = false; // Использовать WebRTC вместо SIPSorcery
        public string? WebRtcWsUri { get; set; } // WebSocket URI для WebRTC (например: "wss://pbx.example.com:8089/ws")
        
        // Call recording settings
        public bool EnableCallRecording { get; set; } = false; // Включить запись звонков
        
        // GitHub update settings
        public string? GitHubRepositoryLink { get; set; } // Ссылка на GitHub репозиторий (например: https://github.com/username/repo)
        public string? GitHubTokenEncrypted { get; set; } // Зашифрованный GitHub Personal Access Token
    }
}

