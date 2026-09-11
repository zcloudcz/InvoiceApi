using System.Globalization;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.Logging;

namespace Fakvio.MauiApp;

/// <summary>
/// MAUI Blazor Hybrid entry point — creates a native app that hosts Blazor components
/// inside a WebView (WebView2 on Windows, WKWebView on iOS/macOS, Android WebView).
/// All shared UI components and services come from Fakvio.UI.Shared (RCL).
/// </summary>
public static class MauiProgram
{
    /// <summary>
    /// Configures and builds the MAUI application with Blazor Hybrid support.
    /// This is the MAUI equivalent of WebAssemblyHostBuilder in the WASM project.
    /// </summary>
    public static Microsoft.Maui.Hosting.MauiApp CreateMauiApp()
    {
        var builder = Microsoft.Maui.Hosting.MauiApp.CreateBuilder();

        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                // OpenSans — default font bundled with the app
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Add the BlazorWebView control — enables hosting Razor components in a native WebView
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        // Developer tools for debugging Blazor in MAUI (hot reload, diagnostics)
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        // Register all shared UI services (MudBlazor, auth, localization, API services, etc.)
        // Same call as in the WASM Program.cs — ensures both hosts use identical DI setup.
        builder.Services.AddSharedUiServices();

        // Configure named HttpClient for API communication.
        // IMPORTANT: In MAUI, localhost doesn't work on mobile devices/emulators.
        // Android emulator uses 10.0.2.2 to reach the host machine's localhost.
        // For production, this should point to the deployed API URL.
        var apiBaseUrl = GetApiBaseUrl();

        builder.Services.AddHttpClient("InvoiceAPI", client =>
        {
            client.BaseAddress = new Uri(apiBaseUrl);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<UnauthorizedRedirectHandler>();

        // Set invariant culture for number/date formatting consistency
        // (same as WASM — prevents locale-specific decimal separator issues)
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

        return builder.Build();
    }

    /// <summary>
    /// Returns the API base URL appropriate for the current platform.
    /// On Android emulator, localhost is not reachable — use 10.0.2.2 instead.
    /// On Windows/macOS/iOS simulator, localhost works normally.
    /// Override with INVOICEAPI_BASE_URL environment variable for production.
    /// </summary>
    private static string GetApiBaseUrl()
    {
        // Allow environment variable override (useful for CI/CD and production)
        var envUrl = Environment.GetEnvironmentVariable("INVOICEAPI_BASE_URL");
        if (!string.IsNullOrEmpty(envUrl))
            return envUrl;

        // Default: localhost for development
#if ANDROID
        // Android emulator maps 10.0.2.2 to host machine's localhost
        return "https://10.0.2.2:7001";
#else
        return "https://localhost:7001";
#endif
    }
}
