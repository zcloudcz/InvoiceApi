using AresService;
using Azure.Identity;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.AiProviders;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Logging;
using Fakvio.Infrastructure.Repository;
using Fakvio.Infrastructure.Service;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
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

        // reCAPTCHA v3 verification — validates tokens from Google's invisible captcha.
        // When SecretKey is not configured, verification is skipped (dev mode).
        services.AddHttpClient<ICaptchaService, CaptchaService>();

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

        // Data Protection — used by TwoFactorService for encrypting TOTP secrets and session tokens,
        // and by CredentialProtector for encrypting SMTP passwords, AI API keys, and OAuth tokens at rest.
        //
        // IMPORTANT: PersistKeysToDbContext stores the key ring in the PostgreSQL master database.
        // Without this, every application restart (Azure App Service recycle, Functions cold start,
        // deployment) generates a new key ring and makes ALL previously encrypted credentials
        // (IMAP password, SMTP password, OAuth tokens, TOTP secrets) permanently unreadable.
        // The symptom is AuthenticationException "Incorrect authentication data" on IMAP connect,
        // or login failures for users with 2FA configured — because CredentialProtector.Decrypt()
        // catches CryptographicException and falls back to returning the raw ciphertext as if it
        // were a plain-text legacy value, then passes that garbage to MailKit / SMTP.
        //
        // SetApplicationName ties the key ring to "Fakvio" so that multiple instances (API + Functions)
        // share the same key ring even though they run as separate processes / app service plans.
        // Without this, each app type generates its own independent key ring (the default isolation
        // uses the application's content root path as the discriminator).
        services.AddDataProtection()
            .PersistKeysToDbContext<MasterDbContext>()
            .SetApplicationName("Fakvio");

        // Credential encryption — encrypts sensitive fields (passwords, API keys, OAuth tokens)
        // before storing them in the database, and decrypts on read. Singleton because
        // IDataProtector is thread-safe and the purpose string never changes.
        services.AddSingleton<ICredentialProtector, CredentialProtector>();

        // ── Application Services (with automatic logging proxy) ──────────────
        // AddScopedWithLogging wraps each service in LoggingProxy which automatically
        // logs ENTER/EXIT/FAILED with elapsed time for every method call.
        // This provides unified logging across all services without modifying their code.

        services.AddScopedWithLogging<ITwoFactorService, TwoFactorService>();
        services.AddScopedWithLogging<IClientService, ClientService>();
        services.AddScopedWithLogging<IInvoiceService, InvoiceService>();
        services.AddScopedWithLogging<INumberSequenceService, NumberSequenceService>();
        services.AddScopedWithLogging<IVatRateService, VatRateService>();
        services.AddScopedWithLogging<IAuthService, AuthService>();
        services.AddScopedWithLogging<IUserService, UserService>();
        services.AddScopedWithLogging<ICurrencyService, CurrencyService>();
        services.AddScopedWithLogging<IInvoiceTemplateService, InvoiceTemplateService>();
        services.AddScopedWithLogging<IPdfExportService, PdfExportService>();
        services.AddScopedWithLogging<IIsdocExportService, IsdocExportService>();
        services.AddScopedWithLogging<IQrPaymentService, QrPaymentService>();
        services.AddScopedWithLogging<IEmailService, EmailService>();
        services.AddScopedWithLogging<IContentTemplateService, ContentTemplateService>();
        services.AddScopedWithLogging<IDashboardService, DashboardService>();
        services.AddScopedWithLogging<IReceivedInvoiceService, ReceivedInvoiceService>();
        services.AddScopedWithLogging<IVatReportService, VatReportService>();
        services.AddScopedWithLogging<ISystemConfigurationService, SystemConfigurationService>();

        // Alerts — generic business alerts (e.g., overpaid proforma) displayed on the dashboard.
        services.AddScopedWithLogging<IAlertService, AlertService>();

        // Notifications — per-user in-app notifications (e.g., payment matched).
        services.AddScopedWithLogging<INotificationService, NotificationService>();

        // Payment reminders (dunning) — settings CRUD, dunning job, manual send/cancel.
        services.AddScopedWithLogging<IReminderService, ReminderService>();
        services.AddSingleton<IInterestCalculator, InterestCalculator>();

        // Tax estimation — calculates income tax, social/health insurance for CZ/SK self-employed.
        services.AddScopedWithLogging<ITaxEstimationService, TaxEstimationService>();

        // Reverse charge codes — MFČR číselník for VAT control statement (PDP / kontrolní hlášení).
        services.AddScopedWithLogging<IReverseChargeCodeService, ReverseChargeCodeService>();

        // EPO schema provider — singleton because XmlSchemaSet compilation is expensive
        // and the result is immutable (read-only after Compile()). The provider caches
        // per (formType, year) so each XSD is compiled once per process lifetime.
        services.AddSingleton<IEpoSchemaProvider, EpoSchemaProvider>();

        // Tenant provisioning — creates, migrates, activates/deactivates tenant schemas.
        // SysAdmin uses this through CompanyController to manage tenant lifecycle.
        services.AddScopedWithLogging<ITenantProvisioningService, TenantProvisioningService>();

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
        services.AddScopedWithLogging<ICloudStorageOrchestrator,
            Fakvio.Infrastructure.Service.CloudStorage.CloudStorageOrchestrator>();

        // ── File Storage ───────────────────────────────────────────────────────
        // Azure Blob Storage — primary file storage for tenant file attachments.
        // Resolves connection string from CompanySystemSettings → SystemConfiguration → appsettings.json.
        services.AddScopedWithLogging<IFileStorage,
            Fakvio.Infrastructure.Service.FileStorage.AzureBlobFileStorage>();
        services.AddScopedWithLogging<IFileAttachmentService, FileAttachmentService>();

        // ── Payment Matching (see PLATBY-ZADANI.md) ────────────────────────────
        // Alias generator — singleton because the underlying CSPRNG is thread-safe and
        // the helper holds no state.
        services.AddSingleton<IAliasGenerator, AliasGenerator>();

        // Tenant-scoped mailbox lifecycle (activate/deactivate/regenerate).
        services.AddScopedWithLogging<IBankAccountMailboxService, BankAccountMailboxService>();

        // SysAdmin-scoped settings (IMAP host/creds/interval).
        services.AddScopedWithLogging<IPaymentMatchingSystemSettingsService,
            PaymentMatchingSystemSettingsService>();

        // AI-backed email parser + matching core.
        services.AddScopedWithLogging<IBankEmailParser, AiBankEmailParser>();
        services.AddScopedWithLogging<IPaymentMatchingService, PaymentMatchingService>();
        services.AddScopedWithLogging<IBankTransactionQueryService, BankTransactionQueryService>();

        // Stateless IMAP poll cycle service — shared by:
        //   - ImapPollWorker (BackgroundService in API host)
        //   - PaymentMatchingFunctions.RunImapPoll (Azure Functions TimerTrigger)
        //   - PaymentMatchingSysAdminController.RunNow (HTTP, SysAdmin)
        // See CLAUDE.md "API + Functions duplication" for the deployment story.
        services.AddScopedWithLogging<IImapPollService, ImapPollService>();

        // End-to-end orchestrator — used by the IMAP worker AND tests.
        services.AddScopedWithLogging<IInboundEmailProcessor, InboundEmailProcessor>();

        // Background worker that pulls the central mailbox on a timer.
        // Registered as HostedService so the host lifecycle starts/stops it.
        services.AddHostedService<ImapPollWorker>();

        // ── PDF Text Extraction ──────────────────────────────────────────────
        // Used by the AI chat to extract text from uploaded PDF files.
        services.AddScopedWithLogging<IPdfTextExtractorService, PdfTextExtractorService>();

        // ── Invoice Import (PDF extraction pipeline: QR → AI → Regex) ────
        services.AddScopedWithLogging<IQrCodeExtractor, QrCodeExtractorService>();
        services.AddScopedWithLogging<IInvoiceAiExtractor, InvoiceAiExtractorService>();
        services.AddScopedWithLogging<IInvoiceTextExtractor, InvoiceTextExtractorService>();
        services.AddScopedWithLogging<IInvoiceImportService, InvoiceImportService>();

        // ── AI Chat ────────────────────────────────────────────────────────
        AddAiProviders(services, configuration);
        services.AddScopedWithLogging<ICompanyAiSettingsResolver, CompanyAiSettingsResolver>();
        services.AddScopedWithLogging<IChatService, ChatService>();
        services.AddScopedWithLogging<IChatContextBuilder, ChatContextBuilder>();

        // Chat tools — each tool is registered individually as IChatTool.
        // ChatToolExecutor discovers all tools via IEnumerable<IChatTool>.
        // To add a new tool: implement IChatTool, register here, and it's automatically available.
        services.AddScoped<IChatTool, AresLookupTool>();
        services.AddScoped<IChatTool, CreateClientTool>();
        services.AddScoped<IChatTool, NavigateTool>();
        services.AddScoped<IChatTool, CreateInvoiceTool>();
        services.AddScoped<IChatTool, ImportInvoiceTool>();
        services.AddScoped<IChatTool, ExportInvoiceTool>();

        // Received invoice tools — let the agent look up, list, and search přijaté faktury.
        services.AddScoped<IChatTool, GetReceivedInvoiceTool>();
        services.AddScoped<IChatTool, ListReceivedInvoicesTool>();
        services.AddScoped<IChatTool, SearchReceivedInvoicesTool>();

        // File attachment tools — let the agent attach files to entities and list existing attachments.
        services.AddScoped<IChatTool, AttachFileTool>();
        services.AddScoped<IChatTool, ListAttachmentsTool>();

        services.AddScopedWithLogging<IChatToolExecutor, ChatToolExecutor>();

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
    ///
    /// Authentication modes (controlled by "UseAzureAdAuthentication" config flag):
    /// - false (default, local dev): standard password-based connection string
    /// - true (Azure production): Microsoft Entra ID (Azure AD) token-based authentication
    ///   using DefaultAzureCredential + NpgsqlDataSource with automatic token refresh
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

        // Determine authentication mode:
        // - Local development: password in connection string (UseAzureAdAuthentication = false)
        // - Azure production: Entra ID managed identity token (UseAzureAdAuthentication = true)
        var useAzureAd = configuration.GetValue<bool>("UseAzureAdAuthentication");

        // Build a shared NpgsqlDataSource — THE SINGLE SOURCE for all PostgreSQL connections.
        // When Azure AD is enabled: acquires and refreshes Entra ID access tokens automatically.
        // When password auth: wraps the connection string as-is (still benefits from connection pooling).
        //
        // CRITICAL: This NpgsqlDataSource is registered in DI as a singleton so that ALL code
        // that needs a raw NpgsqlConnection (TenantProvisioningService, LogFlushService, etc.)
        // can get connections via dataSource.OpenConnectionAsync() instead of new NpgsqlConnection().
        // Without this, raw connections to Azure PostgreSQL fail with "no password provided"
        // because the connection string has no password (Azure AD provides the token instead).
        var dataSource = useAzureAd
            ? CreateAzureDataSource(connectionString)
            : new NpgsqlDataSourceBuilder(connectionString).Build();

        // Register NpgsqlDataSource as singleton — inject it wherever raw connections are needed.
        // This ensures Azure AD token auth works everywhere, not just in DbContext queries.
        services.AddSingleton(dataSource);

        // MasterDbContext — "public" schema containing Users, Companies, CompanySystemSettings, code tables.
        // EnableRetryOnFailure handles transient PostgreSQL/Azure errors (network blips,
        // connection pool exhaustion, failovers) by automatically retrying failed operations.
        services.AddDbContext<MasterDbContext>(options =>
        {
            ConfigureNpgsql(options, dataSource);

            // EF Core 10 throws PendingModelChangesWarning by default when the current model
            // doesn't exactly match the latest migration snapshot. This blocks MigrateAsync()
            // even if the differences are cosmetic (e.g., environment-specific metadata).
            // Downgrade from Throw → Log so migrations proceed and the warning is still visible.
            options.ConfigureWarnings(w =>
                w.Ignore(RelationalEventId.PendingModelChangesWarning));
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
            ConfigureNpgsql(options, dataSource);

            // Custom model cache: one cached model per schema (tenant_42, tenant_99, etc.)
            options.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();

            // Downgrade PendingModelChangesWarning from Throw → Log (same as MasterDbContext above).
            options.ConfigureWarnings(w =>
                w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });
    }

    /// <summary>
    /// Creates an NpgsqlDataSource that uses Microsoft Entra ID (Azure AD) for authentication.
    /// Uses DefaultAzureCredential which supports:
    /// - Managed Identity (App Service, Azure Functions — zero-config in Azure)
    /// - Azure CLI credential (local dev with `az login`)
    /// - Visual Studio / VS Code credential
    ///
    /// The token is automatically refreshed every 55 minutes (Azure AD tokens expire at 60 min).
    /// If a refresh fails, it retries every 10 seconds until successful.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string WITHOUT password.</param>
    /// <returns>NpgsqlDataSource configured with periodic token refresh.</returns>
    private static NpgsqlDataSource CreateAzureDataSource(string connectionString)
    {
        // DefaultAzureCredential tries multiple credential sources in order:
        // 1. Environment variables (AZURE_CLIENT_ID, etc.)
        // 2. Managed Identity (automatic in App Service / Functions)
        // 3. Azure CLI (`az login` — useful for local dev against Azure DB)
        // 4. Visual Studio / VS Code credentials
        var credential = new DefaultAzureCredential();

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

        // UsePeriodicPasswordProvider replaces the static password with a dynamically
        // acquired Azure AD access token. Npgsql calls this callback:
        // - Once at startup to get the initial token
        // - Every 55 minutes (successRefreshInterval) to refresh before expiry
        // - Every 10 seconds (failureRefreshInterval) if the previous refresh failed
        dataSourceBuilder.UsePeriodicPasswordProvider(
            async (_, cancellationToken) =>
            {
                // Request an access token for Azure Database for PostgreSQL.
                // The scope "https://ossrdbms-aad.database.windows.net/.default" is the
                // standard Azure resource identifier for PostgreSQL Flexible Server.
                var tokenRequest = new Azure.Core.TokenRequestContext(
                    ["https://ossrdbms-aad.database.windows.net/.default"]);

                var token = await credential.GetTokenAsync(tokenRequest, cancellationToken);
                return token.Token;
            },
            successRefreshInterval: TimeSpan.FromMinutes(55),  // Refresh 5 min before expiry
            failureRefreshInterval: TimeSpan.FromSeconds(10)); // Retry quickly on failure

        return dataSourceBuilder.Build();
    }

    /// <summary>
    /// Configures Npgsql for a DbContext using the shared NpgsqlDataSource.
    /// The data source handles both Azure AD token auth and password auth transparently.
    /// Centralizes shared Npgsql options (migrations assembly, retry policy).
    /// </summary>
    /// <param name="options">EF Core DbContext options builder.</param>
    /// <param name="dataSource">Shared NpgsqlDataSource (always non-null — built for either auth mode).</param>
    private static void ConfigureNpgsql(
        DbContextOptionsBuilder options,
        NpgsqlDataSource dataSource)
    {
        options.UseNpgsql(dataSource, b =>
        {
            b.MigrationsAssembly("Fakvio.Infrastructure");
            b.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorCodesToAdd: null);
        });
    }
}
