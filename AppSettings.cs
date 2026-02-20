using System;
using Newtonsoft.Json;

namespace Softphone
{
    public class AppSettings
    {
        public string? SipServer { get; set; }
        public string? SipUsername { get; set; }

        // Legacy plaintext password (kept only for backward compatibility / migration).
        // Do not persist it when null so settings.json doesn't contain "SipPassword": null.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SipPassword { get; set; }

        public string? SipPasswordEncrypted { get; set; } // Зашифрованный пароль SIP (PBX)
        
        // Audio device settings
        public string? MicrophoneDeviceGuid { get; set; }
        public string? SpeakerDeviceGuid { get; set; }
        public int? MicrophoneDeviceNumber { get; set; }
        public int? SpeakerDeviceNumber { get; set; }

        // Ringtone settings (incoming call ringtone)
        // WASAPI device id (MMDevice.ID). Empty/null means default output device.
        public string? RingtoneOutputDeviceId { get; set; }
        // 0.0 .. 1.0
        public double RingtoneVolume { get; set; } = 0.7;
        // "wav" (prefer ./ringtone/incoming_call.wav) or "tone" (built-in)
        public string RingtoneSoundMode { get; set; } = "wav";
        
        // Audio codec settings
        public string AudioCodec { get; set; } = "PCMU"; // По умолчанию G.711 μ-law (PCMU)
        public int AudioSampleRate { get; set; } = 16000; // Частота дискретизации (16000 для лучшего качества)
        public int AudioBitrate { get; set; } = 64000; // Битрейт для Opus
        
        // Audio processing settings
        public bool EnableEchoCancellation { get; set; } = true; // Включить акустическое эхоподавление (по умолчанию включено)
        
        // WebRTC settings
        public bool UseWebRtcAudio { get; set; } = false; // Использовать WebRTC вместо SIPSorcery
        public string? WebRtcWsUri { get; set; } // WebSocket URI для WebRTC (например: "wss://pbx.example.com:8089/ws")
        /// <summary>
        /// Включить детализированное логирование WebRTC (JS + C#).
        /// В продакшене можно выключить, чтобы снизить шум в логах.
        /// </summary>
        public bool EnableWebRtcDebug { get; set; } = true;
        
        // Call recording settings
        public bool EnableCallRecording { get; set; } = false; // Включить запись звонков
        
        // GitHub update settings
        public string? GitHubRepositoryLink { get; set; } // Ссылка на GitHub репозиторий (например: https://github.com/username/repo)
        public string? GitHubTokenEncrypted { get; set; } // Зашифрованный GitHub Personal Access Token

        // UI theme: "system" | "dark" | "light"
        public string ThemeMode { get; set; } = "system";

        // AmoCRM integration settings
        public bool EnableAmoCrmIntegration { get; set; } = false; // Включить интеграцию с AmoCRM
        public string? AmoCrmSubdomain { get; set; } // Поддомен AmoCRM (например: "yourcompany" для yourcompany.amocrm.ru)
        public string? AmoCrmAccessTokenEncrypted { get; set; } // Зашифрованный Access Token для AmoCRM API (Manual Token mode)
        public bool EnableAmoCrmLeadSelection { get; set; } = false; // Ручной выбор лида для загрузки записей
        // ...удалено: ShowFirstLeadAfterCall...
        
        // AmoCRM OAuth settings
        public string AmoCrmAuthMode { get; set; } = "manual"; // "manual" или "oauth"
        public string? AmoCrmClientId { get; set; } // OAuth Client ID
        public string? AmoCrmClientSecretEncrypted { get; set; } // Зашифрованный OAuth Client Secret
        public string? AmoCrmRedirectUri { get; set; } // OAuth Redirect URI (например: "callspire://oauth/callback")
        public string? AmoCrmOAuthAccessTokenEncrypted { get; set; } // Зашифрованный OAuth Access Token
        public string? AmoCrmOAuthRefreshTokenEncrypted { get; set; } // Зашифрованный OAuth Refresh Token
        public DateTime? AmoCrmOAuthTokenExpiresAt { get; set; } // Время истечения OAuth токена
    }
}

