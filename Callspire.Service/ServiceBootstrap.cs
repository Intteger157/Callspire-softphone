using System;
using Softphone.Audio;

namespace Softphone.Service
{
    /// <summary>
    /// Headless counterpart of the desktop <c>CrossPlatformBootstrap</c>: wires Core hooks for the
    /// PortAudio/NAudio stack only (no WASAPI, no WPF theme service). Must run before the controller is created.
    /// </summary>
    internal static class ServiceBootstrap
    {
        private static bool _initialized;

        public static void Initialize(ServiceDispatcher dispatcher)
        {
            if (_initialized) return;
            _initialized = true;

            UiThread.Post = dispatcher.Post;
            UiThread.PostUrgent = dispatcher.Post;

            _ = FileLogService.Instance;

            CallStatisticsService.RecordingDurationResolver = path =>
            {
                try { using var reader = new NAudio.Wave.AudioFileReader(path); return reader.TotalTime; }
                catch { return TimeSpan.Zero; }
            };

            AudioDeviceFactory.Default = new PortAudioDeviceFactory();

            RtpCallRecorderFactory.Create = () => new RtpCallRecorder();
            RtpCallRecorderFactory.TryRecoverWavFromOrphanPcm = path => RtpCallRecorder.TryRecoverWavFromOrphanPcm(path);

            TonePlayerFactory.Create = () => new PortAudioTonePlayer();
            RingtoneControl.StopHook = () => PortAudioTonePlayer.Shared.Stop();
            RingbackToneControl.StopHook = () => { };
            MicrophoneControl.OptimizeForVoIPHook = () => { };
            MicrophoneControl.SetMuteHook = _ => { };
        }
    }
}
