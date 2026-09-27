using Fakvio.UI.Shared.Services;
using Blazored.LocalStorage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        // AuthApiService derives from ApiClientBase — register via AddApiClient so login/
        // registration failures are forwarded to the server-side AppLog like all other API errors.
        services.AddApiClient<AuthApiService>();
        services.AddScoped<AuthenticationStateProvider, CustomAuthenticationStateProvider>();

        // API services — all inherit ApiClientBase for shared auth, logging, and impersonation.
        // Each service uses IHttpClientFactory to get the named "InvoiceAPI" HttpClient.
        // AddApiClient<T> wires the IClientLogger into every ApiClientBase-derived service so any
        // exception caught in the base class is forwarded to the server-side AppLog table.
        // Without this wrapper, errors would only land in the browser console.
        services.AddApiClient<VatRateApiService>();
        services.AddApiClient<ClientApiService>();
        services.AddApiClient<FakvioService>();
        services.AddApiClient<UserApiService>();
        services.AddApiClient<CompanyApiService>();
        services.AddApiClient<DashboardApiService>();
        // Feeds ReadinessBanner on the dashboard and the invoice detail (issue #215).
        services.AddApiClient<ReadinessApiService>();
        services.AddApiClient<InvoiceTemplateApiService>();
        services.AddApiClient<ContentTemplateApiService>();
        services.AddApiClient<NumberSequenceApiService>();
        services.AddApiClient<CurrencyApiService>();
        services.AddScoped<GridStateService>();
        services.AddApiClient<CompanySettingsApiService>();
        services.AddApiClient<SystemConfigurationApiService>();
        // Editable AI assistant instructions — SysAdmin-only page /ai-instructions.
        services.AddApiClient<AiInstructionsApiService>();
        services.AddApiClient<AppLogApiService>();
        services.AddApiClient<TwoFactorApiService>();
        services.AddApiClient<CloudStorageApiService>();
        services.AddApiClient<ChatApiService>();
        services.AddApiClient<ReceivedInvoiceApiService>();
        services.AddApiClient<VatReportApiService>();
        services.AddApiClient<TaxApiService>();
        services.AddApiClient<ImportApiService>();
        services.AddApiClient<EmailAdminApiService>();
        services.AddApiClient<FileAttachmentApiService>();
        services.AddApiClient<ReminderApiService>();
        services.AddApiClient<RecurringInvoiceApiService>();

        // Personal API keys for AI clients (MCP) — page /settings/integrations.
        services.AddApiClient<ApiKeyApiService>();

        // OAuth 2.1 consent screen (ADR 0001) — page /oauth/consent.
        services.AddApiClient<OAuthConsentApiService>();
        // "Připojené aplikace" (ADR 0001 §4.8) — page /settings/integrations.
        services.AddApiClient<OAuthGrantsApiService>();

        // User preferences — API client + session cache used by FakvioGrid (page size)
        services.AddApiClient<UserPreferencesApiService>();
        services.AddScoped<UserPreferencesState>();

        // Payment matching — tenant user + SysAdmin.
        services.AddApiClient<PaymentMatchingApiService>();
        services.AddApiClient<PaymentMatchingSysAdminApiService>();
        services.AddApiClient<RecognizedCounterpartyApiService>();

        // Alerts — "Upozornění" tile and per-invoice resolve action.
        services.AddApiClient<AlertApiService>();

        // Notifications — per-user in-app notifications (bell icon, dropdown, page).
        services.AddApiClient<NotificationApiService>();

        // Invoice email — mailbox management + inbox list.
        services.AddApiClient<InvoiceMailboxApiService>();
        services.AddApiClient<InboundInvoiceEmailApiService>();

        // Reverse charge codes — MFČR číselník for the PDP dropdown in invoice line-item editor.
        // Read-only on the client side; admin CRUD is task #49 (ReverseChargeCodes.razor).
        services.AddApiClient<ReverseChargeCodeApiService>();

        // Shared app state — cross-component notifications (e.g., company list changed → refresh dropdown)
        services.AddScoped<AppStateService>();

        // Forwards UI errors to the server log so they reach the AppLog table.
        // Without this, anything caught in the WASM client only shows in the browser console.
        services.AddScoped<IClientLogger, ClientLoggerService>();

        // Central handler for catch blocks in pages/components: shows the snackbar AND forwards
        // client-origin exceptions to AppLog (skips ApiException — ApiClientBase already logged it).
        services.AddScoped<IUiErrorHandler, UiErrorHandler>();

        return services;
    }

    /// <summary>
    /// Registers an ApiClientBase-derived service and wires the IClientLogger into it after
    /// construction. Equivalent to <c>services.AddScoped&lt;T&gt;()</c> plus a property-style
    /// injection of the remote logger via <see cref="ApiClientBase.WithClientLogger"/>.
    ///
    /// This avoids editing every existing ApiService constructor — they remain unchanged.
    /// If the IClientLogger is unregistered (e.g., test host that doesn't add UI services),
    /// the service still works; logs just stay local.
    /// </summary>
    private static IServiceCollection AddApiClient<T>(this IServiceCollection services)
        where T : ApiClientBase
    {
        services.AddScoped<T>(sp =>
        {
            var instance = ActivatorUtilities.CreateInstance<T>(sp);
            var clientLogger = sp.GetService<IClientLogger>();
            if (clientLogger != null)
            {
                instance.WithClientLogger(clientLogger);
            }
            return instance;
        });
        return services;
    }
}
