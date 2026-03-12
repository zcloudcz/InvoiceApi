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
    // Chat streaming with local LLMs (Ollama) can take 2+ minutes for the first pass
    // (non-streaming tool-check call). Default 100s timeout is too short.
    client.Timeout = TimeSpan.FromMinutes(5);
})
// CorrelationIdHandler runs first: adds X-Correlation-Id to every outgoing request.
// UnauthorizedRedirectHandler runs second: intercepts 401 responses for auto-redirect.
.AddHttpMessageHandler<CorrelationIdHandler>()
.AddHttpMessageHandler<UnauthorizedRedirectHandler>();

var host = builder.Build();

// UI is always Czech (cs-CZ). Per-client document language is configured on Client detail page.
// No need to read from localStorage — the LanguageSwitcher has been removed.
CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("cs-CZ");

// Czech culture for date formatting (dd.MM.yyyy) in MudDatePicker and other date inputs.
// Override the number format to use '.' as decimal separator — this prevents issues
// with decimal parsing in numeric inputs (VAT rates "21.0", prices, etc.).
var czechCulture = new CultureInfo("cs-CZ");
czechCulture.NumberFormat.NumberDecimalSeparator = ".";
czechCulture.NumberFormat.CurrencyDecimalSeparator = ".";
CultureInfo.DefaultThreadCurrentCulture = czechCulture;

await host.RunAsync();
