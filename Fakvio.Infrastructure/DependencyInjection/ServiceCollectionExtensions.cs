using AresService;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.AiProviders;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Logging;
using Fakvio.Infrastructure.Repository;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace Fakvio.Infrastructure.DependencyInjection;

/// <summary>
/// Shared service registrations used by both Fakvio.API and Fakvio.Functions.
/// This eliminates code duplication between the two hosting models (Kestrel vs Azure Functions).
///
/// Registers:
/// - MasterDbContext + TenantDbContext (multi-tenant PostgreSQL with schema-per-tenant)
/// - All application services (Client, Invoice, Auth, Email, PDF, QR, etc.)
/// - Cloud storage (Google Drive, OneDrive)
/// - ARES (Czech business registry) HTTP client
/// - Database logging provider
/// - ZMapper source-generated mapping
/// - Data Protection (for TOTP secret encryption)
///
/// Does NOT register (host-specific):
/// - Controllers (API uses AddControllers; Functions adds AddApplicationPart)
/// - Swagger/OpenAPI (API-only)
/// - CORS (API-only; Functions uses Azure host config)
/// - IHostedService for log flush/cleanup (API uses BackgroundService; Functions uses timer triggers)
/// - Middleware pipeline (each host configures its own pipeline)
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all shared infrastructure services needed by the Fakvio application.
    /// Call this from both API and Functions Program.cs to keep DI registrations in sync.
    ///
    /// Example usage in API:
    ///   builder.Services.AddFakvioCore(builder.Configuration);
    ///
    /// Example usage in Functions:
    ///   services.AddFakvioCore(config);
    /// </summary>
    /// <param name="services">The DI container to register services into.</param>
    /// <param name="configuration">Application configuration (appsettings / local.settings).</param>
    /// <returns>The same IServiceCollection for chaining.</returns>
    public static IServiceCollection AddFakvioCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Database Configuration ──────────────────────────────────────────
        // Multi-tenant architecture with two DbContexts in a single PostgreSQL database:
        // - MasterDbContext: "public" schema for Users, Companies, CompanySystemSettings, code tables
        // - TenantDbContext: per-tenant schema (e.g., "tenant_42") for invoices, clients, etc.

        AddDatabaseContexts(services, configuration);

        // ── HTTP Clients ────────────────────────────────────────────────────

        // ARES (Czech business registry) — validates Czech company ICO numbers
        services.AddHttpClient<IAresService, AresServiceImpl>();

        // Paylibo.com — Czech QR Platba (SPD) generation from domestic bank account format
        services.AddHttpClient<IPayliboClient, PayliboClient>();

        // ── Core Infrastructure ─────────────────────────────────────────────

        // HttpContextAccessor — required by CurrentUserService and HttpContextTenantResolver
        // to read JWT claims (UserId, CompanyId) from the current HTTP request
        services.AddHttpContextAccessor();

        // CurrentUserService — extracts authenticated user ID from JWT for audit trail fields
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        // TenantResolver — extracts CompanyId from JWT for multi-tenant database routing
        services.AddScoped<ITenantResolver, HttpContextTenantResolver>();

        // TenantDbContextFactory — creates properly-connected TenantDbContext instances
        // at runtime based on CompanySystemSettings in the master database
        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();

        // ARES cache repository — caches Czech business registry lookups to avoid API rate limits
        services.AddScoped<IAresCacheRepository, AresCacheRepository>();

        // ZMapper — source-generated object mapping (compile-time, zero reflection overhead)
        services.AddZMapper();

        // Data Protection — used by TwoFactorService for encrypting TOTP secrets and session tokens
        services.AddDataProtection();

        // ── Application Services ────────────────────────────────────────────

        services.AddScoped<ITwoFactorService, TwoFactorService>();
        services.AddScoped<IClientService, ClientService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<INumberSequenceService, NumberSequenceService>();
        services.AddScoped<IVatRateService, VatRateService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ICurrencyService, CurrencyService>();
        services.AddScoped<IInvoiceTemplateService, InvoiceTemplateService>();
        services.AddScoped<IPdfExportService, PdfExportService>();
        services.AddScoped<IQrPaymentService, QrPaymentService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IContentTemplateService, ContentTemplateService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<IReceivedInvoiceService, ReceivedInvoiceService>();
        services.AddScoped<IVatReportService, VatReportService>();
        services.AddScoped<ISystemConfigurationService, SystemConfigurationService>();

        // Tax estimation — calculates income tax, social/health insurance for CZ/SK self-employed.
        services.AddScoped<ITaxEstimationService, TaxEstimationService>();

        // Tenant provisioning — creates, migrates, activates/deactivates tenant schemas.
        // SysAdmin uses this through CompanyController to manage tenant lifecycle.
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        // ── Cloud Storage ───────────────────────────────────────────────────
        // Google Drive and OneDrive registered as IExternalCloudStorage.
        // CloudStorageOrchestrator iterates IEnumerable<IExternalCloudStorage>
        // and routes to the correct provider based on CompanySystemSettings.
        services.AddHttpClient<Fakvio.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>();
        services.AddScoped<IExternalCloudStorage>(sp =>
            sp.GetRequiredService<Fakvio.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>());
        services.AddHttpClient<Fakvio.Infrastructure.Service.CloudStorage.OneDriveStorageService>();
        services.AddScoped<IExternalCloudStorage>(sp =>
            sp.GetRequiredService<Fakvio.Infrastructure.Service.CloudStorage.OneDriveStorageService>());
        services.AddScoped<ICloudStorageOrchestrator,
            Fakvio.Infrastructure.Service.CloudStorage.CloudStorageOrchestrator>();

        // ── PDF Text Extraction ──────────────────────────────────────────────
        // Used by the AI chat to extract text from uploaded PDF files.
        services.AddScoped<IPdfTextExtractorService, PdfTextExtractorService>();

        // ── Invoice Import (PDF extraction pipeline: QR → AI → Regex) ────
        // QR code extraction from PDF images — uses iText7 + ZXing to find and decode QR codes.
        services.AddScoped<IQrCodeExtractor, QrCodeExtractorService>();
        // AI-based invoice data extraction — uses the default AI provider to parse PDF text.
        // Falls back gracefully if no AI provider is configured.
        services.AddScoped<IInvoiceAiExtractor, InvoiceAiExtractorService>();
        // Regex-based invoice data extraction — last resort fallback when QR and AI fail.
        services.AddScoped<IInvoiceTextExtractor, InvoiceTextExtractorService>();
        // Import orchestrator — waterfall pipeline + validation + client resolution.
        services.AddScoped<IInvoiceImportService, InvoiceImportService>();

        // ── AI Chat ────────────────────────────────────────────────────────
        AddAiProviders(services, configuration);
        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IChatContextBuilder, ChatContextBuilder>();

        // Chat tools — each tool is registered individually as IChatTool.
        // ChatToolExecutor discovers all tools via IEnumerable<IChatTool>.
        // To add a new tool: implement IChatTool, register here, and it's automatically available.
        services.AddScoped<IChatTool, AresLookupTool>();
        services.AddScoped<IChatTool, CreateClientTool>();
        services.AddScoped<IChatTool, NavigateTool>();
        services.AddScoped<IChatTool, CreateInvoiceTool>();
        services.AddScoped<IChatToolExecutor, ChatToolExecutor>();

        // ── Database Logging ────────────────────────────────────────────────
        // Structured logging to AppLog table in master DB.
        // Uses ConcurrentQueue for non-blocking enqueue; flushed by:
        // - API: LogFlushService (IHostedService, every 5 seconds)
        // - Functions: LogFlush timer trigger (every 5 seconds)
        services.AddSingleton<ILoggerProvider>(new DatabaseLoggerProvider(LogLevel.Information));

        return services;
    }

    /// <summary>
    /// Registers AI providers based on configuration.
    /// Only providers with valid API keys (or base URLs for Ollama) are registered.
    /// This prevents runtime errors from unconfigured providers.
    /// </summary>
    private static void AddAiProviders(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind AiSettings from configuration.
        services.Configure<AiSettings>(configuration.GetSection("AiSettings"));

        var aiSettings = configuration.GetSection("AiSettings").Get<AiSettings>() ?? new AiSettings();

        // Register only providers that have valid configuration.
        // Each provider is registered as IAiProvider (multiple registrations → IEnumerable<IAiProvider>).

        if (!string.IsNullOrEmpty(aiSettings.Claude.ApiKey))
        {
            services.AddSingleton<IAiProvider, ClaudeProvider>();
        }

        if (!string.IsNullOrEmpty(aiSettings.OpenAI.ApiKey))
        {
            services.AddSingleton<IAiProvider, OpenAiProvider>();
        }

        if (!string.IsNullOrEmpty(aiSettings.Gemini.ApiKey))
        {
            services.AddHttpClient<GeminiProvider>();
            services.AddSingleton<IAiProvider>(sp => sp.GetRequiredService<GeminiProvider>());
        }

        if (!string.IsNullOrEmpty(aiSettings.Ollama.BaseUrl))
        {
            services.AddHttpClient<OllamaProvider>();
            services.AddSingleton<IAiProvider>(sp => sp.GetRequiredService<OllamaProvider>());
        }

        // Factory resolves providers by name from the registered IEnumerable<IAiProvider>.
        services.AddSingleton<IAiProviderFactory, AiProviderFactory>();
    }

    /// <summary>
    /// Registers MasterDbContext and TenantDbContext with the PostgreSQL (Npgsql) provider.
    /// Single database, schema-per-tenant isolation:
    /// - MasterDbContext uses the "public" schema (default PostgreSQL schema)
    /// - TenantDbContext dynamically sets its schema per-request from CompanySystemSettings.SchemaName
    /// </summary>
    private static void AddDatabaseContexts(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // Shared connection string — single PostgreSQL database for all schemas.
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Database connection string 'DefaultConnection' not configured. " +
                "Set it in appsettings.json, local.settings.json, or environment variable " +
                "ConnectionStrings__DefaultConnection.");

        // MasterDbContext — "public" schema containing Users, Companies, CompanySystemSettings, code tables.
        // EnableRetryOnFailure handles transient PostgreSQL/Azure errors (network blips,
        // connection pool exhaustion, failovers) by automatically retrying failed operations.
        services.AddDbContext<MasterDbContext>(options =>
        {
            options.UseNpgsql(connectionString, b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
            });

            // EF Core 10 throws PendingModelChangesWarning by default when the current model
            // doesn't exactly match the latest migration snapshot. This blocks MigrateAsync()
            // even if the differences are cosmetic (e.g., environment-specific metadata).
            // Downgrade from Throw → Log so migrations proceed and the warning is still visible.
            options.ConfigureWarnings(w =>
                w.Log(RelationalEventId.PendingModelChangesWarning));
        });

        // TenantDbContext — per-tenant schema for invoices, clients, templates, etc.
        // Registered as scoped; the schema is resolved DYNAMICALLY per request.
        // 1. If the user has a CompanyId claim → look up CompanySystemSettings in master DB
        //    → set the schema name on the TenantDbContext (same database, different schema).
        // 2. If no CompanyId (startup, migrations, EF CLI tools) → no schema set (uses default).
        // This ensures each request targets the CORRECT tenant schema.
        //
        // IMPORTANT: ReplaceService<IModelCacheKeyFactory> ensures EF Core caches a separate
        // compiled model per schema. Without this, all tenants would share the first tenant's
        // model and query the wrong schema. See TenantModelCacheKeyFactory for details.
        services.AddDbContext<TenantDbContext>((serviceProvider, options) =>
        {
            // All tenants share the same PostgreSQL connection — schema isolation, not DB isolation.
            options.UseNpgsql(connectionString, b =>
            {
                b.MigrationsAssembly("Fakvio.Infrastructure");
                b.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorCodesToAdd: null);
            });

            // Custom model cache: one cached model per schema (tenant_42, tenant_99, etc.)
            options.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();

            // Downgrade PendingModelChangesWarning from Throw → Log (same as MasterDbContext above).
            options.ConfigureWarnings(w =>
                w.Log(RelationalEventId.PendingModelChangesWarning));
        });
    }
}
