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

        // SIP transport security (TLS)
        // When enabled, SIP signalling uses TLS (typically port 5061) instead of UDP.
        public bool SipUseTls { get; set; } = false;

        // SIP media security (SRTP). Applies to SIP mode only (not WebRTC).
        public bool SipUseSrtp { get; set; } = false;
        
        // Connection display names (for UI)
        public string? MainConnectionName { get; set; } // Пользовательское название основного подключения
        public string? SecondaryConnectionName { get; set; } // Пользовательское название второго подключения
        
        // Second SIP connection settings (transport now configurable: Sip | WebRtc)
        public string? SipServer2 { get; set; }
        public string? SipUsername2 { get; set; }
        public string? SipPasswordEncrypted2 { get; set; } // Зашифрованный пароль для второго SIP подключения
        public string? RtpServer2 { get; set; } // RTP сервер для второго подключения (если отличается от SIP сервера, например для Beeline: 62.105.133.230)

        public bool SipUseTls2 { get; set; } = false;

        public bool SipUseSrtp2 { get; set; } = false;

        // Per-connection transport. Values: "Sip" | "WebRtc".
        // Migration: when MainConnectionTransport is unset and legacy UseWebRtcAudio==true,
        // MainConnectionTransport is set to "WebRtc" on first load (see AppSettingsMigration).
        public string MainConnectionTransport { get; set; } = "Sip";
        public string SecondaryConnectionTransport { get; set; } = "Sip";

        // Secondary connection WebRTC config (mirror of primary)
        public string? WebRtcWsUri2 { get; set; }
        /// <summary>WebRTC SIP-auth username for line 2 (may differ from <see cref="SipUsername2"/>).</summary>
        public string? WebRtcUsername2 { get; set; }
        public string? WebRtcPasswordEncrypted2 { get; set; }
        public string? SecondaryWebRtcTurnUri { get; set; }
        public string? SecondaryWebRtcTurnUsername { get; set; }

        /// <summary>Legacy plaintext TURN password for line 2 (migrated to <see cref="SecondaryWebRtcTurnPasswordEncrypted"/>).</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? SecondaryWebRtcTurnPassword { get; set; }

        public string? SecondaryWebRtcTurnPasswordEncrypted { get; set; }
        
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
        /// <summary>WebRTC SIP-auth username for main line (may differ from <see cref="SipUsername"/>).</summary>
        public string? WebRtcUsername { get; set; }
        public string? WebRtcPasswordEncrypted { get; set; }
        /// <summary>
        /// Включить детализированное логирование WebRTC (JS + C#).
        /// В продакшене можно выключить, чтобы снизить шум в логах.
        /// </summary>
        public bool EnableWebRtcDebug { get; set; } = true;
        
        /// <summary>
        /// (Legacy) Пользовательский TURN‑сервер для WebRTC.
        /// Не используется в UI: TURN перенесён в настройки каждого подключения.
        /// Оставлено для обратной совместимости со старыми settings.json.
        /// </summary>
        public string? WebRtcTurnUri { get; set; }
        
        /// <summary>
        /// (Legacy) Имя пользователя для аутентификации на TURN‑сервере (опционально).
        /// </summary>
        public string? WebRtcTurnUsername { get; set; }
        
        /// <summary>
        /// (Legacy) Пароль для аутентификации на TURN‑сервере (опционально).
        /// Migrated to <see cref="MainWebRtcTurnPasswordEncrypted"/>.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? WebRtcTurnPassword { get; set; }

        // Per-connection TURN (preferred)
        public string? MainWebRtcTurnUri { get; set; }
        public string? MainWebRtcTurnUsername { get; set; }

        /// <summary>Legacy plaintext TURN password (migrated to <see cref="MainWebRtcTurnPasswordEncrypted"/>).</summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? MainWebRtcTurnPassword { get; set; }

        public string? MainWebRtcTurnPasswordEncrypted { get; set; }

        /// <summary>Last applied TURN config revision from PBX Gateway (<c>config_revision</c>).</summary>
        public string? MainWebRtcTurnGatewayRevision { get; set; }
        // NOTE: Secondary connection is SIP-only; TURN/ICE does not apply there.
        
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
        public bool EnableAmoCrmLeadSelection { get; set; } = false; // Ручной выбор лида (local upload или gateway process-call)
        public bool EnableAmoCrmRecordingUpload { get; set; } = true; // Автоматическая загрузка аудиозаписей в Kommo
        // ...удалено: ShowFirstLeadAfterCall...
        
        // AmoCRM OAuth settings
        public string AmoCrmAuthMode { get; set; } = "manual"; // "manual" или "oauth"
        public string? AmoCrmClientId { get; set; } // OAuth Client ID
        public string? AmoCrmClientSecretEncrypted { get; set; } // Зашифрованный OAuth Client Secret
        public string? AmoCrmRedirectUri { get; set; } // OAuth Redirect URI (например: "callspire://oauth/callback")
        public string? AmoCrmOAuthAccessTokenEncrypted { get; set; } // Зашифрованный OAuth Access Token
        public string? AmoCrmOAuthRefreshTokenEncrypted { get; set; } // Зашифрованный OAuth Refresh Token
        public DateTime? AmoCrmOAuthTokenExpiresAt { get; set; } // Время истечения OAuth токена

        /// <summary>
        /// When both local Kommo settings and gateway Kommo are available: "local" or "gateway".
        /// </summary>
        public string? AmoCrmConnectionSource { get; set; }

        /// <summary>
        /// Where CRM upload runs: "local" (desktop AmoCrmService) or "gateway" (PBX Gateway process-call).
        /// Independent from <see cref="AmoCrmConnectionSource"/>.
        /// </summary>
        public string? AmoCrmRecordingUploadSource { get; set; }

        // Callspire PBX Gateway (JWT API on/near MikoPBX — CDR, recordings, originate, WebRTC admin, …)
        public bool EnableMikoPbxCdr { get; set; } = false;
        public string? MikoPbxCdrServiceUrl { get; set; } // Gateway base URL (e.g. https://pbx.example.com:8443)
        public string? MikoPbxCdrTokenEncrypted { get; set; } // JWT-токен (зашифрован DPAPI)
        public string? MikoPbxExtension { get; set; } // Внутренний номер (например "204")

        // MikoPBX Originate / CallerID selection
        public string? SelectedOutboundCallerId { get; set; } // Last selected CallerID from dropdown
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public List<string>? CachedOutboundCallerIds { get; set; } // Cached list from proxy for offline

        /// <summary>Username used for WebRTC SIP auth when <see cref="WebRtcUsername"/> is unset (legacy).</summary>
        public static string? EffectiveMainWebRtcUsername(AppSettings? s)
        {
            if (s == null) return null;
            string? u = string.IsNullOrWhiteSpace(s.WebRtcUsername) ? s.SipUsername : s.WebRtcUsername.Trim();
            if (string.IsNullOrWhiteSpace(u)) return null;
            return u.EndsWith("-WS", StringComparison.OrdinalIgnoreCase) ? u[..^3] : u;
        }

        /// <summary>Username used for secondary WebRTC when <see cref="WebRtcUsername2"/> is unset (legacy).</summary>
        public static string? EffectiveSecondaryWebRtcUsername(AppSettings? s) =>
            s == null ? null : (string.IsNullOrWhiteSpace(s.WebRtcUsername2) ? s.SipUsername2 : s.WebRtcUsername2.Trim());

        /// <summary>Encrypted password field used for main WebRTC when WebRTC-specific is unset (legacy).</summary>
        public static string? EffectiveMainWebRtcPasswordEncrypted(AppSettings? s) =>
            s == null ? null : (string.IsNullOrWhiteSpace(s.WebRtcPasswordEncrypted) ? s.SipPasswordEncrypted : s.WebRtcPasswordEncrypted);

        /// <summary>Encrypted password field used for secondary WebRTC when WebRTC-specific is unset (legacy).</summary>
        public static string? EffectiveSecondaryWebRtcPasswordEncrypted(AppSettings? s) =>
            s == null ? null : (string.IsNullOrWhiteSpace(s.WebRtcPasswordEncrypted2) ? s.SipPasswordEncrypted2 : s.WebRtcPasswordEncrypted2);

        /// <summary>
        /// True when line 2 should use WebRTC over WSS.
        /// Explicit <c>WebRtc</c> counts only if WSS URI and WebRTC credentials are present; otherwise SIP init can run when SIP fields exist.
        /// For <c>Sip</c> or missing/empty transport (JSON often omits the property and C# defaults to <c>Sip</c>),
        /// WebRTC is inferred only when a real ws(s) URI and credentials exist but SIP registrar fields do not.
        /// </summary>
        public static bool SecondaryLineUsesWebRtc(AppSettings? settings)
        {
            if (settings == null) return false;

            if (string.Equals(settings.SecondaryConnectionTransport, "WebRtc", StringComparison.OrdinalIgnoreCase))
            {
                return HasMeaningfulWebRtcWsUri(settings.WebRtcWsUri2) && HasSecondaryWebRtcCredentialBundle(settings);
            }

            if (string.IsNullOrWhiteSpace(settings.SecondaryConnectionTransport) ||
                string.Equals(settings.SecondaryConnectionTransport, "Sip", StringComparison.OrdinalIgnoreCase))
                return InferSecondaryWebRtcFromLegacyFields(settings);

            return false;
        }

        private static bool InferSecondaryWebRtcFromLegacyFields(AppSettings settings)
        {
            if (!HasSecondaryWebRtcCredentialBundle(settings))
                return false;
            if (HasSecondarySipRegistrarBundle(settings))
                return false;
            return HasMeaningfulWebRtcWsUri(settings.WebRtcWsUri2);
        }

        private static bool HasSecondarySipRegistrarBundle(AppSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.SipServer2) &&
            !string.IsNullOrWhiteSpace(settings.SipUsername2) &&
            !string.IsNullOrWhiteSpace(settings.SipPasswordEncrypted2);

        private static bool HasSecondaryWebRtcCredentialBundle(AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(EffectiveSecondaryWebRtcUsername(settings)))
                return false;
            return !string.IsNullOrWhiteSpace(EffectiveSecondaryWebRtcPasswordEncrypted(settings));
        }

        /// <summary>
        /// Non-placeholder ws: or wss: URI suitable for JsSIP.
        /// </summary>
        public static bool HasMeaningfulWebRtcWsUri(string? ws)
        {
            string? t = ws?.Trim();
            if (string.IsNullOrEmpty(t)) return false;
            if (!t.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) &&
                !t.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                return false;
            if (t.Equals("wss://", StringComparison.OrdinalIgnoreCase) ||
                t.Equals("ws://", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
    }
}

