using System;
using System.Threading.Tasks;
using Android;
using Android.Content.PM;
using AndroidX.Core.App;
using AndroidX.Core.Content;

namespace Softphone.Android
{
    /// <summary>
    /// Runtime permission helper for VoIP microphone access (Android 6+).
    /// Call <see cref="EnsureRecordAudioAsync"/> before the first call attempt.
    /// </summary>
    public static class AudioPermissionHelper
    {
        public const int RecordAudioRequestCode = 1001;

        private static TaskCompletionSource<bool>? _pendingTcs;

        public static Task<bool> EnsureRecordAudioAsync(global::Android.App.Activity activity)
        {
            if (ContextCompat.CheckSelfPermission(activity, Manifest.Permission.RecordAudio)
                == Permission.Granted)
                return Task.FromResult(true);

            if (_pendingTcs != null)
                return _pendingTcs.Task;

            _pendingTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            ActivityCompat.RequestPermissions(
                activity,
                new[] { Manifest.Permission.RecordAudio },
                RecordAudioRequestCode);

            return _pendingTcs.Task;
        }

        public static void OnRequestPermissionsResult(
            int requestCode,
            string[] permissions,
            Permission[] grantResults)
        {
            if (requestCode != RecordAudioRequestCode || _pendingTcs == null)
                return;

            var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            _pendingTcs.TrySetResult(granted);
            _pendingTcs = null;

            AppLog.Log(granted
                ? "[AudioPermissionHelper] RECORD_AUDIO granted"
                : "[AudioPermissionHelper] RECORD_AUDIO denied by user");
        }
    }
}
