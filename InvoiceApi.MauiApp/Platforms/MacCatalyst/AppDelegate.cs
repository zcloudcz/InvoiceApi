using Foundation;

namespace InvoiceApi.MauiApp.Platforms.MacCatalyst;

/// <summary>
/// macOS Catalyst app delegate — the entry point for the macOS app.
/// Uses Mac Catalyst to run the iPad version as a native macOS application.
/// </summary>
[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    protected override Microsoft.Maui.Hosting.MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
