using System.Globalization;
using InvoiceApi.BlazorUI.Components;
using InvoiceApi.BlazorUI.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Localization;
using MudBlazor.Services;

// Set invariant culture for number/date formatting so that decimal values (e.g., VAT rate "21.0")
// are parsed correctly regardless of server locale. Without this, Czech locale expects comma (21,0)
// and throws FormatException when receiving dot-separated decimals from JSON/JS.
// NOTE: Only set CurrentCulture (formatting), NOT CurrentUICulture (resource lookup).
// Setting UICulture to InvariantCulture would prevent IStringLocalizer from finding .resx files.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add MudBlazor services
builder.Services.AddMudServices();

// Add localization services.
// NOTE: Do NOT set ResourcesPath = "Resources" — the .resx files are co-located with
// SharedResource.cs (marker class) in the Resources/ folder, and .NET SDK uses the marker
// class namespace (not folder path) for the embedded resource name. Setting ResourcesPath
// would add an extra "Resources." to the lookup path, causing a mismatch.
builder.Services.AddLocalization();

// Add authentication with a default scheme.
// Blazor Server uses its own AuthenticationStateProvider (not cookie middleware),
// but the authorization middleware still needs a registered default scheme to avoid
// "No DefaultChallengeScheme found" errors on [Authorize] endpoints.
builder.Services.AddAuthentication("BlazorServer")
    .AddCookie("BlazorServer", options =>
    {
        // Override the default /Account/Login redirect to match our Blazor login page
        options.LoginPath = "/login";
    });
builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();

// Configure HttpClient for API
builder.Services.AddHttpClient("InvoiceAPI", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiSettings:BaseUrl"] ?? "https://localhost:7001");
    client.DefaultRequestHeaders.Add("Accept", "application/json");
});

// Register authentication services
builder.Services.AddScoped<AuthApiService>();
builder.Services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();

// Register API services — all inherit ApiClientBase for shared auth, logging, and impersonation
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Request localization middleware — determines UI language in this priority order:
// 1. Cookie (explicit user choice via language switcher) — persists across sessions
// 2. Accept-Language header (browser auto-detection) — CZ/SK browsers get Czech, others get English
// 3. Default fallback: English (en-US) for unknown/unsupported browser languages
var supportedCultures = new[] { new CultureInfo("cs-CZ"), new CultureInfo("en-US") };
app.UseRequestLocalization(new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en-US"),
    SupportedCultures = supportedCultures,
    SupportedUICultures = supportedCultures,
    RequestCultureProviders = new List<IRequestCultureProvider>
    {
        // 1st priority: cookie set by the language switcher component
        new CookieRequestCultureProvider(),
        // 2nd priority: browser Accept-Language header (auto-detects cs/sk → cs-CZ, en → en-US)
        new AcceptLanguageHeaderRequestCultureProvider()
    }
});

app.UseAntiforgery();

app.MapStaticAssets();

// Minimal API endpoint for changing the UI culture.
// The language switcher navigates to this URL — it sets a cookie and redirects back.
app.MapGet("/Culture/SetCulture", (HttpContext context, string culture, string redirectUri) =>
{
    context.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true }
    );
    return Results.LocalRedirect(redirectUri);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
