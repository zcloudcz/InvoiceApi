namespace InvoiceApi.MauiApp;

/// <summary>
/// MAUI Application class — creates the main window and sets the initial page.
/// This is the native app shell; Blazor components are rendered inside MainPage's BlazorWebView.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Creates the main application window with MainPage as the root content.
    /// MainPage contains the BlazorWebView that hosts all Razor components.
    /// </summary>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage()) { Title = "InvoiceApi" };
    }
}
