using Fakvio.UI.Shared.Services;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using MudBlazor.Services;

namespace Fakvio.UI.Shared;

/// <summary>
/// Centralized DI registration for all shared UI services.
/// Called by both BlazorUI (WASM) and MauiApp (MAUI Hybrid) hosts
/// to avoid duplicating service registrations in multiple Program.cs files.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all shared UI services: MudBlazor, localization, authorization,
    /// localStorage, HTTP handlers, API services, and app state.
    /// The HttpClient named "InvoiceAPI" must be configured by the host before calling this method
    /// (because the base URL differs between WASM and MAUI).
    /// </summary>
    /// <param name="services">The DI service collection from the host project.</param>
    /// <returns>The same service collection for fluent chaining.</returns>
    public static IServiceCollection AddSharedUiServices(this IServiceCollection services)
    {
        // MudBlazor component library services (dialogs, snackbar, etc.)
        services.AddMudServices();

        // Localization services for multi-language UI (cs-CZ / en-US).
        // ResourcesPath = "Resources" tells the ResourceManagerStringLocalizerFactory that .resx
        // files live in the Resources/ subfolder. Without this, the factory looks for embedded
        // resource "Fakvio.UI.Shared.SharedResource" but the actual name is
        // "Fakvio.UI.Shared.Resources.SharedResource" (because the file is in Resources/).
        services.AddLocalization(options => options.ResourcesPath = "Resources");

        // Authorization services — required for <AuthorizeView>, [Authorize] etc.
        services.AddAuthorizationCore();

        // Blazored.LocalStorage — stores JWT tokens in browser localStorage.
        // Works in both WASM (browser) and MAUI (WebView2/WKWebView has localStorage).
        services.AddBlazoredLocalStorage();

        // HTTP message handlers — registered as transient (one per request pipeline).
        // CorrelationIdHandler: adds X-Correlation-Id header to every outgoing request.
        services.AddTransient<CorrelationIdHandler>();
        // UnauthorizedRedirectHandler: intercepts 401 responses and redirects to /login.
        services.AddTransient<UnauthorizedRedirectHandler>();

        // Authentication services
        services.AddScoped<AuthApiService>();
        services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();

        // API services — all inherit ApiClientBase for shared auth, logging, and impersonation.
        // Each service uses IHttpClientFactory to get the named "InvoiceAPI" HttpClient.
        services.AddScoped<VatRateApiService>();
        services.AddScoped<ClientApiService>();
        services.AddScoped<FakvioService>();
        services.AddScoped<UserApiService>();
        services.AddScoped<CompanyApiService>();
        services.AddScoped<DashboardApiService>();
        services.AddScoped<InvoiceTemplateApiService>();
        services.AddScoped<ContentTemplateApiService>();
        services.AddScoped<NumberSequenceApiService>();
        services.AddScoped<CurrencyApiService>();
        services.AddScoped<CompanySettingsApiService>();
        services.AddScoped<SystemConfigurationApiService>();
        services.AddScoped<AppLogApiService>();
        services.AddScoped<TwoFactorApiService>();
        services.AddScoped<CloudStorageApiService>();

        // Shared app state — cross-component notifications (e.g., company list changed → refresh dropdown)
        services.AddScoped<AppStateService>();

        return services;
    }
}
