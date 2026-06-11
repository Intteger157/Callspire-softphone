using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;

namespace Softphone.Android
{
    /// <summary>
    /// Primary launcher activity. Avalonia renders into this activity's content view.
    /// Uses Avalonia 11 bootstrap (AvaloniaMainActivity&lt;App&gt;); migrate to
    /// Avalonia 12 AvaloniaAndroidApplication pattern when targeting net10.0-android.
    /// </summary>
    [Activity(
        Label = "Callspire",
        Theme = "@style/MyTheme.NoActionBar",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges = ConfigChanges.Orientation
            | ConfigChanges.ScreenSize
            | ConfigChanges.UiMode
            | ConfigChanges.ScreenLayout
            | ConfigChanges.SmallestScreenSize)]
    public class MainActivity : AvaloniaMainActivity<App>
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            AndroidBootstrap.AttachActivity(this);
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            return builder
                .UseAndroid()
                .LogToTrace();
        }

        public override void OnRequestPermissionsResult(
            int requestCode,
            string[] permissions,
            Permission[] grantResults)
        {
            AudioPermissionHelper.OnRequestPermissionsResult(requestCode, permissions, grantResults);
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        }
    }
}
