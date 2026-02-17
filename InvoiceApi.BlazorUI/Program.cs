using System.Globalization;
using Blazored.LocalStorage;
using InvoiceApi.BlazorUI;
using InvoiceApi.BlazorUI.Components;
using InvoiceApi.BlazorUI.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using MudBlazor.Services;

// Standalone Blazor WebAssembly entry point.
// Unlike Blazor Server, this runs entirely in the browser — no SignalR circuits.
// All API calls go through HttpClient to the InvoiceApi.API backend.

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Register root Blazor components.
// #app is the target element in wwwroot/index.html.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Add MudBlazor component library services (dialogs, snackbar, etc.)
builder.Services.AddMudServices();

// Add localization services for multi-language UI (cs-CZ / en-US).
// ResourcesPath is left empty (default) because SharedResource marker class is in the root
// namespace (InvoiceApi.BlazorUI), and .resx DependentUpon derives the embedded resource name
// from the C# type → "InvoiceApi.BlazorUI.SharedResource.resources". Setting ResourcesPath
// to "Resources" would make the localizer look for ".Resources.SharedResource" — a mismatch.
builder.Services.AddLocalization();

// Authorization services — required for <AuthorizeView>, [Authorize] etc. in WASM
builder.Services.AddAuthorizationCore();

// NOTE: We do NOT call AddCascadingAuthenticationState() here because App.razor already
// wraps the Router in <CascadingAuthenticationState>. Using both the service-level registration
// AND the component wrapper can cause conflicts in standalone WASM (duplicate cascading values).

// Blazored.LocalStorage — stores JWT tokens in browser localStorage
// (replaces ProtectedSessionStorage which is server-only)
builder.Services.AddBlazoredLocalStorage();

// Configure named HttpClient for API communication.
// BaseUrl is read from wwwroot/appsettings.json → ApiSettings:BaseUrl
builder.Services.AddHttpClient("InvoiceAPI", client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["ApiSettings:BaseUrl"]!);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Register authentication services
builder.Services.AddScoped<AuthApiService>();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();

// Register API services — all inherit ApiClientBase for shared auth, logging, and impersonation.
// Each service uses IHttpClientFactory to get the named "InvoiceAPI" HttpClient.
builder.Services.AddScoped<VatRateApiService>();
builder.Services.AddScoped<ClientApiService>();
builder.Services.AddScoped<InvoiceApiService>();
builder.Services.AddScoped<UserApiService>();
builder.Services.AddScoped<CompanyApiService>();
builder.Services.AddScoped<DashboardApiService>();
builder.Services.AddScoped<InvoiceTemplateApiService>();
builder.Services.AddScoped<ContentTemplateApiService>();
builder.Services.AddScoped<NumberSequenceApiService>();
builder.Services.AddScoped<CurrencyApiService>();
builder.Services.AddScoped<CompanySettingsApiService>();
builder.Services.AddScoped<SystemConfigurationApiService>();
builder.Services.AddScoped<AppLogApiService>();
builder.Services.AddScoped<TwoFactorApiService>();
builder.Services.AddScoped<CloudStorageApiService>();

var host = builder.Build();

// Restore the user's preferred culture from localStorage before rendering.
// This runs once at app startup — subsequent changes are handled by LanguageSwitcher.
// Uses the blazorCulture JS helper (defined in index.html) instead of calling localStorage
// directly, to avoid potential "this" binding issues in WASM JS interop.
var jsRuntime = host.Services.GetRequiredService<IJSRuntime>();
try
{
    // Read the culture value that was saved by LanguageSwitcher
    var storedCulture = await jsRuntime.InvokeAsync<string?>("blazorCulture.get");
    if (!string.IsNullOrEmpty(storedCulture))
    {
        var culture = new CultureInfo(storedCulture);
        // Set UI culture for resource file lookup (cs-CZ → Czech .resx, en-US → English .resx)
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
catch
{
    // Ignore — localStorage might not be available (e.g., during prerendering)
}

// Set invariant culture for number/date formatting so that decimal values (e.g., VAT rate "21.0")
// are parsed correctly regardless of browser locale. Without this, Czech locale expects comma (21,0)
// and throws FormatException when receiving dot-separated decimals from JSON/JS.
// NOTE: Only set CurrentCulture (formatting), NOT CurrentUICulture (resource lookup).
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

await host.RunAsync();
