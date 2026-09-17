using System;
using System.IO;
using System.Runtime.InteropServices;
using Softphone.Audio;

namespace Softphone
{
    /// <summary>
    /// Platform-agnostic DI / hook wiring that must run before any service or
    /// window is created.  Called from both the WPF startup (App.xaml.cs, Windows)
    /// and the Avalonia startup (Program.cs, macOS/Linux).
    ///
    /// Intentionally free of any WPF, NAudio WASAPI, or WebView2 references so
    /// that it compiles and runs on every supported target framework.
    /// </summary>
    public static class CrossPlatformBootstrap
    {
        private static bool _initialized;

        public static void Initialize(Action<Action> postToUiThread, Action<Action> postUrgentToUiThread)
        {
            if (_initialized) return;
            _initialized = true;

            // ── UiThread hooks (replaces WPF Dispatcher coupling in Core) ──────
            UiThread.Post       = postToUiThread;
            UiThread.PostUrgent = postUrgentToUiThread;

            // ── File logging ──────────────────────────────────────────────────
            _ = FileLogService.Instance;

            // ── Recording duration resolver (NAudio cross-platform WAV reader) ──
            CallStatisticsService.RecordingDurationResolver = path =>
            {
                try
                {
                    using var reader = new NAudio.Wave.AudioFileReader(path);
                    return reader.TotalTime;
                }
                catch
                {
                    return TimeSpan.Zero;
                }
            };

            // ── Audio device factory ──────────────────────────────────────────
            // DesktopAudioDeviceFactory selects WASAPI on Windows and PortAudio
            // everywhere else (macOS / Linux) — already platform-aware.
            AudioDeviceFactory.Default = new DesktopAudioDeviceFactory();

            // ── RTP call recorder (NAudio WAV writer — cross-platform) ────────
            RtpCallRecorderFactory.Create = () => new RtpCallRecorder();
            RtpCallRecorderFactory.TryRecoverWavFromOrphanPcm =
                path => RtpCallRecorder.TryRecoverWavFromOrphanPcm(path);

            // ── Platform-specific audio hook registration ─────────────────────
            if (OperatingSystem.IsWindows())
            {
                RegisterWindowsAudioHooks();
            }
            else
            {
                RegisterPortAudioHooks();
            }

            // ── Tone generator ───────────────────────────────────────────────
            // Windows: NAudio WaveOutEvent (winmm). macOS/Linux: PortAudio-backed player —
            // WaveOutEvent P/Invokes winmm.dll and is not available there.
            if (OperatingSystem.IsWindows())
                TonePlayerFactory.Create = () => new ToneGenerator();
            else
                TonePlayerFactory.Create = () => new PortAudioTonePlayer();

            // ── Theme ─────────────────────────────────────────────────────────
            try { ThemeService.Initialize(); } catch { }
        }

        // ── Windows-specific hooks ────────────────────────────────────────────
        // Called only on Windows via OperatingSystem.IsWindows() check above.
        // The actual implementation lives in Windows-only files (RingtoneService,
        // RingbackToneService, MicrophoneMuteHelper) which are excluded from the
        // net8.0 build.  On Windows, those files ARE compiled, so we call them
        // through delegates captured at startup.
        private static Action? _windowsRingtoneStop;
        private static Action? _windowsRingbackStop;
        private static Action? _windowsMicOptimize;
        private static Action<bool>? _windowsMicMute;

        /// <summary>
        /// Called from App.xaml.cs (Windows only) to register NAudio WASAPI
        /// ringtone / microphone hooks after this bootstrap has run.
        /// </summary>
        public static void RegisterWindowsAudioHooksExternal(
            Action ringtoneStop,
            Action ringbackStop,
            Action micOptimize,
            Action<bool> micMute)
        {
            _windowsRingtoneStop = ringtoneStop;
            _windowsRingbackStop = ringbackStop;
            _windowsMicOptimize  = micOptimize;
            _windowsMicMute      = micMute;

            RingtoneControl.StopHook            = ringtoneStop;
            RingbackToneControl.StopHook        = ringbackStop;
            MicrophoneControl.OptimizeForVoIPHook = micOptimize;
            MicrophoneControl.SetMuteHook       = micMute;
        }

        private static void RegisterWindowsAudioHooks()
        {
            // Hooks registered later by App.xaml.cs after the Windows services
            // are fully constructed (avoids circular initialization).
        }

        private static void RegisterPortAudioHooks()
        {
            // On macOS / Linux ringtone + ringback playback goes through PortAudio
            // (PortAudioTonePlayer). Microphone mute is applied per call by
            // SipService.SetMute / WebRtcService.SetMuteAsync — there is no
            // system-wide mic mute API to mirror the Windows CoreAudio behaviour.
            RingtoneControl.StopHook              = () => PortAudioTonePlayer.Shared.Stop();
            RingbackToneControl.StopHook          = () => { /* per-call ITonePlayer instances stop themselves */ };
            MicrophoneControl.OptimizeForVoIPHook = () => { };
            MicrophoneControl.SetMuteHook         = _ => { };
        }
    }
}
