using System;
using System.Collections.Generic;
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
        
        // Connection display names (for UI)
        public string? MainConnectionName { get; set; } // Пользовательское название основного подключения
        public string? SecondaryConnectionName { get; set; } // Пользовательское название второго подключения
        
        // Second SIP connection settings (SIP only, independent from main connection)
        public string? SipServer2 { get; set; }
        public string? SipUsername2 { get; set; }
        public string? SipPasswordEncrypted2 { get; set; } // Зашифрованный пароль для второго SIP подключения
        public string? RtpServer2 { get; set; } // RTP сервер для второго подключения (если отличается от SIP сервера, например для Beeline: 62.105.133.230)
        
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
        
        /// <summary>
        /// Пользовательский TURN‑сервер для WebRTC (например: "turn:turn.example.com:3478?transport=udp").
        /// Если не задан, используются встроенные STUN/TURN сервера по умолчанию.
        /// </summary>
        public string? WebRtcTurnUri { get; set; }
        
        /// <summary>
        /// Имя пользователя для аутентификации на TURN‑сервере (опционально).
        /// </summary>
        public string? WebRtcTurnUsername { get; set; }
        
        /// <summary>
        /// Пароль для аутентификации на TURN‑сервере (опционально).
        /// </summary>
        public string? WebRtcTurnPassword { get; set; }
        
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

        // MikoPBX CDR integration
        public bool EnableMikoPbxCdr { get; set; } = false;
        public string? MikoPbxCdrServiceUrl { get; set; } // URL прокси-сервиса CDR (например "https://pbx.example.com:8443")
        public string? MikoPbxCdrTokenEncrypted { get; set; } // JWT-токен (зашифрован DPAPI)
        public string? MikoPbxExtension { get; set; } // Внутренний номер (например "204")

        // MikoPBX Originate / CallerID selection
        public string? SelectedOutboundCallerId { get; set; } // Last selected CallerID from dropdown
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? CachedOutboundCallerIds { get; set; } // Cached list from proxy for offline
    }
}

