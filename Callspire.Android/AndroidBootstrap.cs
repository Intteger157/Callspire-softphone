using System;
using Android.App;
using Avalonia.Threading;
using Softphone.Android.Audio;
using Softphone.Android.WebRtc;

namespace Softphone.Android
{
    /// <summary>
    /// Android head startup: wires Core facades (UiThread, audio factory, WebRTC stub).
    /// Called once when Avalonia finishes framework initialization.
    /// </summary>
    public static class AndroidBootstrap
    {
        private static bool _initialized;
        private static Activity? _activity;

        public static Activity? CurrentActivity => _activity;

        public static void AttachActivity(Activity activity)
        {
            _activity = activity;
        }

        public static void OnAvaloniaInitialized()
        {
            if (_initialized) return;
            _initialized = true;

            UiThread.Post       = action => Dispatcher.UIThread.Post(action);
            UiThread.PostUrgent = action => Dispatcher.UIThread.Post(action, DispatcherPriority.Send);

            _ = FileLogService.Instance;

            AudioDeviceFactory.Default = new AndroidAudioDeviceFactory();

            RingtoneControl.StopHook              = () => AppLog.Log("[Android] RingtoneControl.Stop (stub)");
            RingbackToneControl.StopHook          = () => AppLog.Log("[Android] RingbackToneControl.Stop (stub)");
            MicrophoneControl.OptimizeForVoIPHook = () => AppLog.Log("[Android] MicrophoneControl.OptimizeForVoIP (stub)");
            MicrophoneControl.SetMuteHook         = mute => AppLog.Log($"[Android] MicrophoneControl.SetMute({mute}) (stub)");

            WebRtcEngineHostHolder.Current = new AndroidWebRtcEngineHost();

            AppLog.Log("[AndroidBootstrap] Initialized — AudioRecord/AudioTrack factory, SoftwareAec via EchoCancellerFactory");
        }

        /// <summary>
        /// Ensures RECORD_AUDIO is granted before opening capture devices or placing a call.
        /// </summary>
        public static async System.Threading.Tasks.Task<bool> EnsureMicrophonePermissionAsync()
        {
            var activity = _activity;
            if (activity == null)
            {
                AppLog.Log("[AndroidBootstrap] No activity — cannot request RECORD_AUDIO");
                return false;
            }

            return await AudioPermissionHelper.EnsureRecordAudioAsync(activity);
        }
    }
}
