using System.Globalization;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
// Standalone Blazor WebAssembly entry point — thin host.
// All shared UI components, services, and models live in Fakvio.UI.Shared (RCL).
// This file only handles WASM-specific bootstrapping and HttpClient configuration.

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Register root Blazor components from the shared RCL.
// #app is the target element in wwwroot/index.html.
builder.RootComponents.Add<Fakvio.UI.Shared.Components.App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register all shared UI services (MudBlazor, auth, localization, API services, etc.)
// This single call replaces ~30 individual service registrations that were previously here.
builder.Services.AddSharedUiServices();

// Configure named HttpClient for API communication.
// BaseUrl is read from wwwroot/appsettings.json → ApiSettings:BaseUrl.
// Message handlers (CorrelationId, UnauthorizedRedirect) are registered by AddSharedUiServices().
builder.Services.AddHttpClient("InvoiceAPI", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ApiSettings:BaseUrl"]!);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
// CorrelationIdHandler runs first: adds X-Correlation-Id to every outgoing request.
// UnauthorizedRedirectHandler runs second: intercepts 401 responses for auto-redirect.
.AddHttpMessageHandler<CorrelationIdHandler>()
.AddHttpMessageHandler<UnauthorizedRedirectHandler>();

var host = builder.Build();

// UI is always Czech (cs-CZ). Per-client document language is configured on Client detail page.
// No need to read from localStorage — the LanguageSwitcher has been removed.
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("cs-CZ");

// Set invariant culture for number/date formatting so that decimal values (e.g., VAT rate "21.0")
// are parsed correctly regardless of browser locale.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

await host.RunAsync();
