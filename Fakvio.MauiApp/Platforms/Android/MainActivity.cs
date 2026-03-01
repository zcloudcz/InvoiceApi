using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Fakvio.MauiApp.Platforms.Android;

/// <summary>
/// Android main activity — the entry point for the Android app.
/// LaunchMode.SingleTop ensures only one instance of the activity exists.
/// </summary>
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
                           ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
