using System;

namespace Softphone.Audio
{
    /// <summary>
    /// Platform-neutral entry point for the incoming-call ringtone used by the Avalonia call window.
    /// Windows → WASAPI <c>RingtoneService</c>; macOS / Linux → <see cref="PortAudioTonePlayer"/>.
    /// </summary>
    public static class PlatformRingtone
    {
        public static void Configure(AppSettings? settings)
        {
            if (settings == null) return;
            try
            {
#if WINDOWS
                RingtoneService.Instance.Configure(settings.RingtoneOutputDeviceId, (float)settings.RingtoneVolume, settings.RingtoneSoundMode);
#else
                PortAudioTonePlayer.Shared.Configure(settings.SpeakerDeviceNumber ?? -1, (float)settings.RingtoneVolume);
#endif
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PlatformRingtone] Configure failed: {ex.Message}");
            }
        }

        public static void Start()
        {
            try
            {
#if WINDOWS
                RingtoneService.Instance.Start();
#else
                PortAudioTonePlayer.Shared.PlayRingtone();
#endif
            }
            catch (Exception ex)
            {
                AppLog.Log($"[PlatformRingtone] Start failed: {ex.Message}");
            }
        }

        public static void Stop() => RingtoneControl.Stop();
    }
}
