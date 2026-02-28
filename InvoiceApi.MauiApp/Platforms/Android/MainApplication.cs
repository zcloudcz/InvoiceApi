using Android.App;
using Android.Runtime;

namespace InvoiceApi.MauiApp.Platforms.Android;

/// <summary>
/// Android application class — bootstraps the MAUI app.
/// Points to MauiProgram.CreateMauiApp() for DI and service configuration.
/// </summary>
[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override Microsoft.Maui.Hosting.MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
