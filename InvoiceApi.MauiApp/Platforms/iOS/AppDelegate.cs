using Foundation;

namespace InvoiceApi.MauiApp.Platforms.iOS;

/// <summary>
/// iOS app delegate — the entry point for the iOS app.
/// Bootstraps the MAUI app via MauiProgram.CreateMauiApp().
/// </summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override Microsoft.Maui.Hosting.MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
