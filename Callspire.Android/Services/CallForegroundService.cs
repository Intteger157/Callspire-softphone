using System;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;

namespace Softphone.Android.Services
{
    /// <summary>
    /// Keeps the VoIP process alive while a call is active (Android background kill protection).
    /// Start via <see cref="StartCallService"/> before media flows; stop when the call ends.
    /// </summary>
    [Service(
        Exported = false,
        ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone
            | global::Android.Content.PM.ForegroundService.TypePhoneCall)]
    public class CallForegroundService : Service
    {
        public const string ActionStart = "com.callspire.softphone.action.START_CALL";
        public const string ActionStop  = "com.callspire.softphone.action.STOP_CALL";
        public const string ExtraCallerLabel = "caller_label";

        private const int NotificationId   = 2001;
        private const string ChannelId     = "callspire_active_call";

        public override void OnCreate()
        {
            base.OnCreate();
            CreateNotificationChannel();
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == ActionStop)
            {
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            var label = intent?.GetStringExtra(ExtraCallerLabel) ?? "Active call";
            var notification = BuildNotification(label);
            StartForeground(NotificationId, notification);
            AppLog.Log("[CallForegroundService] Started foreground call service");

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy()
        {
            AppLog.Log("[CallForegroundService] Stopped");
            base.OnDestroy();
        }

        public override global::Android.OS.IBinder? OnBind(Intent? intent) => null;

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
                return;

            var channel = new NotificationChannel(
                ChannelId,
                "Active calls",
                NotificationImportance.Low)
            {
                Description = "Shows when Callspire is handling a phone call"
            };

            var manager = GetSystemService(NotificationService) as NotificationManager;
            manager?.CreateNotificationChannel(channel);
        }

        private Notification BuildNotification(string label)
        {
            var pendingIntent = PendingIntent.GetActivity(
                this,
                0,
                new Intent(this, typeof(MainActivity)),
                PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            return new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle("Callspire")
                .SetContentText(label)
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysPhoneCall)
                .SetOngoing(true)
                .SetContentIntent(pendingIntent)
                .SetCategory(NotificationCompat.CategoryCall)
                .Build();
        }

        public static void StartCallService(Context context, string? callerLabel = null)
        {
            var intent = new Intent(context, typeof(CallForegroundService))
                .SetAction(ActionStart);
            if (!string.IsNullOrEmpty(callerLabel))
                intent.PutExtra(ExtraCallerLabel, callerLabel);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                context.StartForegroundService(intent);
            else
                context.StartService(intent);
        }

        public static void StopCallService(Context context)
        {
            var intent = new Intent(context, typeof(CallForegroundService))
                .SetAction(ActionStop);
            context.StartService(intent);
        }
    }
}
