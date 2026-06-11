using System;

namespace Softphone.Audio
{
    /// <summary>
    /// Хук для управления входящим рингтоном из платформо-независимого кода.
    /// Desktop регистрирует RingtoneService.Instance.Stop() на старте приложения.
    /// </summary>
    public static class RingtoneControl
    {
        public static Action? StopHook { get; set; }

        /// <summary>Останавливает воспроизведение рингтона (no-op, если хук не зарегистрирован).</summary>
        public static void Stop()
        {
            try { StopHook?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// Хук для остановки локального гудка КПВ (ringback), проигрываемого платформенным сервисом.
    /// Desktop регистрирует RingbackToneService.Instance.Stop() на старте приложения.
    /// </summary>
    public static class RingbackToneControl
    {
        public static Action? StopHook { get; set; }

        /// <summary>Останавливает воспроизведение ringback-тона (no-op, если хук не зарегистрирован).</summary>
        public static void Stop()
        {
            try { StopHook?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// Хук для платформенного управления микрофоном (mute, оптимизация уровней для VoIP).
    /// Desktop регистрирует реализацию на NAudio (MicrophoneMuteHelper) на старте приложения.
    /// </summary>
    public static class MicrophoneControl
    {
        public static Action? OptimizeForVoIPHook { get; set; }
        public static Action<bool>? SetMuteHook { get; set; }

        /// <summary>Оптимизирует уровни/настройки микрофона для VoIP (no-op без хука).</summary>
        public static void OptimizeForVoIP()
        {
            try { OptimizeForVoIPHook?.Invoke(); } catch { }
        }

        /// <summary>Включает/выключает системный mute микрофона (no-op без хука).</summary>
        public static void SetMute(bool mute)
        {
            try { SetMuteHook?.Invoke(mute); } catch { }
        }
    }
}
