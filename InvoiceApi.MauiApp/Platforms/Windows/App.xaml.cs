namespace InvoiceApi.MauiApp.WinUI;

/// <summary>
/// Windows platform entry point — bootstraps the MAUI app on Windows (WinUI 3).
/// Uses WebView2 (Chromium-based) to render Blazor components.
/// </summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        this.InitializeComponent();
    }

    protected override Microsoft.Maui.Hosting.MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
