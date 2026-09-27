using AresService;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.AiProviders;
using Fakvio.Infrastructure.Authentication;
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
        // The gate fails closed (issue #200): without a usable SecretKey every gated request
        // is rejected, so running without reCAPTCHA needs an explicit Recaptcha:Enabled=false.
        // Short timeout on purpose — fail-closed only helps if it fails fast. The default of
        // 100 s would park every login, registration and ARES lookup for that long whenever
        // Google is unreachable; 5 s turns the same outage into a quick, visible rejection.
        services.AddHttpClient<ICaptchaService, CaptchaService>(
            client => client.Timeout = TimeSpan.FromSeconds(5));

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
        services.AddScopedWithLogging<IUserPreferencesService, UserPreferencesService>();
        services.AddScopedWithLogging<IApiKeyService, ApiKeyService>();
        services.AddScopedWithLogging<IApiKeyAuthenticator, ApiKeyAuthenticator>();
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

        // Tenant readiness — one place that answers "is this tenant set up well enough to invoice?".
        services.AddScopedWithLogging<ITenantReadinessService, TenantReadinessService>();

        // Alerts — generic business alerts (e.g., overpaid proforma) displayed on the dashboard.
        services.AddScopedWithLogging<IAlertService, AlertService>();

        // Notifications — per-user in-app notifications (e.g., payment matched).
        services.AddScopedWithLogging<INotificationService, NotificationService>();

        // Invoice email — receive invoices via email (ISDOC/PDF attachments).
        services.AddScopedWithLogging<IInvoiceMailboxService, InvoiceMailboxService>();
        services.AddScopedWithLogging<IInvoiceEmailProcessor, InvoiceEmailProcessor>();
        services.AddScopedWithLogging<IInvoiceEmailClassifier, InvoiceEmailClassifier>();
        services.AddSingleton<IIsdocImportParser, IsdocImportParser>();

        // Payment reminders (dunning) — settings CRUD, dunning job, manual send/cancel.
        services.AddScopedWithLogging<IReminderService, ReminderService>();
        services.AddSingleton<IInterestCalculator, InterestCalculator>();

        // Recurring invoices — schedule CRUD + generation cycle (RecurringInvoiceWorker calls RunCycleAsync).
        services.AddScopedWithLogging<IRecurringInvoiceService, RecurringInvoiceService>();

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

        // Registry of known counterparty accounts (insurance, tax office, …) for
        // recognizing recurring payments without an invoice.
        services.AddScopedWithLogging<IRecognizedCounterpartyService, RecognizedCounterpartyService>();

        // Stateless IMAP poll cycle service — shared by:
        //   - ImapPollWorker (BackgroundService in API host)
        //   - PaymentMatchingSysAdminController.RunNow (HTTP, SysAdmin)
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
        services.AddScopedWithLogging<IClientCsvImportService, ClientCsvImportService>();

        // ── AI Chat ────────────────────────────────────────────────────────
        AddAiProviders(services, configuration);
        services.AddScopedWithLogging<ICompanyAiSettingsResolver, CompanyAiSettingsResolver>();
        services.AddScopedWithLogging<IChatService, ChatService>();
        services.AddScopedWithLogging<IChatContextBuilder, ChatContextBuilder>();

        // Editable AI instructions — stored in SystemConfiguration, cached in IMemoryCache.
        // ChatContextBuilder reads them from the cache on every chat message; a SysAdmin
        // save invalidates the entry, so edits take effect on the next message.
        // AddMemoryCache is idempotent, so calling it here is safe in every host.
        services.AddMemoryCache();
        services.AddScopedWithLogging<IAiInstructionsService, AiInstructionsService>();

        // Chat tools — each tool is registered individually as IChatTool.
        // ChatToolExecutor discovers all tools via IEnumerable<IChatTool>.
        // To add a new tool: implement IChatTool, register here, and it's automatically available.
        services.AddScoped<IChatTool, AresLookupTool>();
        services.AddScoped<IChatTool, CreateClientTool>();
        services.AddScoped<IChatTool, NavigateTool>();
        services.AddScoped<IChatTool, CreateInvoiceTool>();
        services.AddScoped<IChatTool, ImportInvoiceTool>();
        services.AddScoped<IChatTool, ExportInvoiceTool>();

        // Client tools — list/detail plus the two writes, both behind the confirm gate (#222).
        services.AddScoped<IChatTool, ListClientsTool>();
        services.AddScoped<IChatTool, GetClientTool>();
        services.AddScoped<IChatTool, UpdateClientTool>();
        services.AddScoped<IChatTool, DeleteClientTool>();

        // Received invoice tools — let the agent look up, list, and search přijaté faktury.
        services.AddScoped<IChatTool, GetReceivedInvoiceTool>();
        services.AddScoped<IChatTool, ListReceivedInvoicesTool>();
        services.AddScoped<IChatTool, SearchReceivedInvoicesTool>();

        // …and act on them: record a new one and walk it through its lifecycle.
        // All four write, so all four are IConfirmableChatTool — see DEVGUIDE §4.7 rule 7.
        services.AddScoped<IChatTool, CreateReceivedInvoiceTool>();
        services.AddScoped<IChatTool, ApproveReceivedInvoiceTool>();
        services.AddScoped<IChatTool, MarkReceivedInvoicePaidTool>();
        services.AddScoped<IChatTool, DeleteReceivedInvoiceTool>();

        // File attachment tools — let the agent attach files to entities and list existing attachments.
        services.AddScoped<IChatTool, AttachFileTool>();
        services.AddScoped<IChatTool, ListAttachmentsTool>();

        // Reporting tools — read-only overviews (dashboard, receivables, period/client, DPH).
        services.AddScoped<IChatTool, GetDashboardTool>();
        services.AddScoped<IChatTool, ListInvoicesTool>();
        services.AddScoped<IChatTool, GetVatReportTool>();

        // Company profile tools — read and change the issuer ("naše firma") and its bank
        // accounts. Every write among them is confirmable (IConfirmableChatTool).
        services.AddScoped<IChatTool, GetMyCompanyTool>();
        services.AddScoped<IChatTool, UpdateMyCompanyTool>();
        services.AddScoped<IChatTool, AddBankAccountTool>();
        services.AddScoped<IChatTool, UpdateBankAccountTool>();
        services.AddScoped<IChatTool, DeleteBankAccountTool>();

        // Template tools — invoice DATA blueprints (read-only) and the PDF / e-mail HTML
        // templates, whose default per type and language is changeable behind the confirm gate.
        services.AddScoped<IChatTool, ListInvoiceTemplatesTool>();
        services.AddScoped<IChatTool, GetInvoiceTemplateTool>();
        services.AddScoped<IChatTool, ListContentTemplatesTool>();
        services.AddScoped<IChatTool, GetContentTemplateTool>();
        services.AddScoped<IChatTool, SetDefaultContentTemplateTool>();

        // Setup tools — let the agent say what is still missing before the user can invoice.
        services.AddScoped<IChatTool, GetReadinessTool>();

        // Issued invoice lifecycle — read the detail, then issue / mark paid / send / delete.
        // The four data-changing ones are IConfirmableChatTool: the model must show a preview
        // and get the user's approval before anything is written (DEVGUIDE §4.7).
        services.AddScoped<IChatTool, GetInvoiceTool>();
        services.AddScoped<IChatTool, CompleteInvoiceTool>();
        services.AddScoped<IChatTool, MarkInvoicePaidTool>();
        services.AddScoped<IChatTool, SendInvoiceEmailTool>();
        services.AddScoped<IChatTool, DeleteInvoiceTool>();

        // Numbering and VAT settings — read the číselné řady and sazby DPH, and change them
        // conversationally. Both writes of each pair are confirmable (IConfirmableChatTool).
        services.AddScoped<IChatTool, ListNumberSequencesTool>();
        services.AddScoped<IChatTool, CreateNumberSequenceTool>();
        services.AddScoped<IChatTool, UpdateNumberSequenceTool>();
        services.AddScoped<IChatTool, ListVatRatesTool>();
        services.AddScoped<IChatTool, CreateVatRateTool>();
        services.AddScoped<IChatTool, UpdateVatRateTool>();

        // Reminder (dunning) tools — read the state and the settings; the settings write is
        // confirmable (IConfirmableChatTool). Sending and cancelling reminders stays in the UI.
        services.AddScoped<IChatTool, ListRemindersTool>();
        services.AddScoped<IChatTool, GetReminderSettingsTool>();
        services.AddScoped<IChatTool, UpdateReminderSettingsTool>();

        // Payment tools — read-only by decision of story #149: matching a payment to an invoice
        // moves money between documents, so it stays on the Payments page.
        services.AddScoped<IChatTool, ListPaymentsTool>();
        services.AddScoped<IChatTool, GetPaymentTool>();

        services.AddScopedWithLogging<IChatToolExecutor, ChatToolExecutor>();

        // ── Database Logging ────────────────────────────────────────────────
        // Structured logging to AppLog table in master DB.
        // Uses ConcurrentQueue for non-blocking enqueue; flushed by
        // LogFlushService (IHostedService in the API host, every 5 seconds).
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
    /// Writes the resolved database auth mode to the host log. Call it from BOTH hosts right
    /// after Build() and before any database work (Fakvio.API and Fakvio.Functions Program.cs).
    ///
    /// Why this exists: the health endpoint that reports the same two values is SysAdmin-only,
    /// and signing in needs the master database (that is where users live). So in the one
    /// scenario where the auth mode matters most — the database is unreachable — the endpoint
    /// cannot answer, while the process itself starts up fine (Resolve/Validate never connect).
    /// This log line is then the only place an operator can read which mode the process picked
    /// and which configuration key won. SELFHOST-DB.md points at it.
    ///
    /// SECURITY: mode and source only. The connection string must never reach a log — in
    /// Password mode it carries the password.
    /// </summary>
    /// <param name="services">The built application service provider.</param>
    public static void LogDatabaseAuthMode(this IServiceProvider services)
    {
        var options = services.GetRequiredService<DatabaseOptions>();

        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(DatabaseAuthModeLogCategory)
            .LogInformation(
                "Startup: database auth mode {AuthMode} (source: {AuthModeSource})",
                options.AuthMode,
                options.AuthModeSource);
    }

    /// <summary>
    /// Log category of the startup line above. Named rather than derived from a type so an
    /// operator can filter on it and so the runbook can quote it verbatim.
    /// </summary>
    private const string DatabaseAuthModeLogCategory = "Fakvio.Infrastructure.Database";

    /// <summary>
    /// Registers MasterDbContext and TenantDbContext with the PostgreSQL (Npgsql) provider.
    /// Single database, schema-per-tenant isolation:
    /// - MasterDbContext uses the "public" schema (default PostgreSQL schema)
    /// - TenantDbContext dynamically sets its schema per-request from CompanySystemSettings.SchemaName
    ///
    /// Authentication mode is resolved by <see cref="DatabaseOptions.Resolve"/> (new
    /// "Database:AuthMode" config, falling back to the legacy "UseAzureAdAuthentication" bool):
    /// - Password (default, local dev): standard password-based connection string
    /// - AzureEntraId (Azure production): Microsoft Entra ID (Azure AD) token-based
    ///   authentication using DefaultAzureCredential + NpgsqlDataSource with automatic
    ///   token refresh
    /// </summary>
    private static void AddDatabaseContexts(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // Resolve + fail-fast validate at the composition root — a missing/invalid connection
        // string or an auth-mode mismatch (e.g. password in an AzureEntraId connection string)
        // crashes at startup with an actionable message instead of failing later with an
        // opaque Npgsql error on the first request.
        var options = DatabaseOptions.Resolve(configuration);
        options.Validate();

        // Single factory that owns every NpgsqlDataSource the app uses (Root for the request
        // path, plus on-demand per-schema sources for provisioning/migration). See
        // INpgsqlDataSourceFactory XML doc for the ownership rule.
        var factory = new NpgsqlDataSourceFactory(options);

        // The resolved instance is the ONLY registration on purpose. A
        // services.Configure<DatabaseOptions>(configuration.GetSection("Database")) used to sit
        // here as well, and it was a trap: IOptions<DatabaseOptions> would re-bind the raw
        // section, so in production (where the mode comes from the legacy
        // "UseAzureAdAuthentication" key) it reported Password while this singleton — the one
        // that actually built the data source — said AzureEntraId. Injecting DatabaseOptions
        // directly is what the diagnostic health endpoint relies on.
        services.AddSingleton(options);

        // Registered with a FACTORY DELEGATE (`_ => factory`), not a bare instance
        // (AddSingleton<TService>(instance)). This matters: Microsoft.Extensions.DependencyInjection
        // only disposes singletons it considers itself to have created — a delegate registration
        // qualifies, a pre-built instance registration does not. BUT the container only "creates"
        // a delegate-registered singleton the first time something RESOLVES it (constructor
        // injection or GetRequiredService/GetService) — a delegate that never runs has nothing
        // for the container to dispose. As of this PR, nothing in production code resolves
        // INpgsqlDataSourceFactory (the first consumer arrives with #134's TenantProvisioningService
        // change), so today this line does NOT yet give the factory a disposal path at host
        // shutdown — factory.Dispose()/DisposeAsync() simply never run, and the process exit is
        // what actually reclaims Root. Once #134 injects the factory somewhere, resolution happens
        // and the disposal path described on INpgsqlDataSourceFactory's XML doc becomes real. See
        // NpgsqlDataSourceFactoryTests.ContainerDisposesFactory_WhenRegisteredViaFactoryDelegate_AndResolved
        // for what this proves (delegate registration + resolution => disposed) and what it does not
        // (it does not prove today's composition root disposes anything, because nothing resolves
        // the factory here). Its sibling
        // ContainerDoesNotDisposeInstance_WhenRegisteredViaBareInstance_EvenIfResolved pins the
        // contrasting rule that the `AddSingleton(factory.Root)` line below relies on, and
        // ContainerDoesNotDisposeFactory_WhenRegisteredViaDelegate_ButNeverResolved pins today's
        // actual state (delegate registered, never resolved => never disposed).
        services.AddSingleton<INpgsqlDataSourceFactory>(_ => factory);

        // AddSingleton(factory.Root) is kept ON PURPOSE — it is the public contract 9 existing
        // consumers rely on by injecting NpgsqlDataSource directly (AzureOperationController,
        // TimerFunctions, LogFlushService, LogCleanupService, ImapPollService, AdvisoryLock,
        // TenantDbContextFactory, TenantProvisioningService + tests). Keeping this registration
        // means none of them need to change.
        //
        // Unlike the factory registration above, THIS is a pre-built instance registration —
        // the container never disposes it. That is intentional: Root has a single owner (the
        // factory, which disposes it in its own Dispose/DisposeAsync), so it is disposed exactly
        // once even though it is exposed here for direct injection. NpgsqlDataSource.Dispose()
        // is also verified idempotent (NpgsqlDataSourceFactoryTests.Root_DisposedDirectly_ThenFactoryDisposedToo_DoesNotThrow)
        // in case any consumer disposes it despite the documented "callers never dispose" rule.
        services.AddSingleton(factory.Root);

        var dataSource = factory.Root;

        // MasterDbContext — "public" schema containing Users, Companies, CompanySystemSettings, code tables.
        // EnableRetryOnFailure handles transient PostgreSQL/Azure errors (network blips,
        // connection pool exhaustion, failovers) by automatically retrying failed operations.
        services.AddDbContext<MasterDbContext>(dbOptions =>
        {
            ConfigureNpgsql(dbOptions, dataSource, options);

            // EF Core 10 throws PendingModelChangesWarning by default when the current model
            // doesn't exactly match the latest migration snapshot. This blocks MigrateAsync()
            // even if the differences are cosmetic (e.g., environment-specific metadata).
            // Downgrade from Throw → Log so migrations proceed and the warning is still visible.
            dbOptions.ConfigureWarnings(w =>
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
        services.AddDbContext<TenantDbContext>((serviceProvider, dbOptions) =>
        {
            // All tenants share the same PostgreSQL connection — schema isolation, not DB isolation.
            ConfigureNpgsql(dbOptions, dataSource, options);

            // Custom model cache: one cached model per schema (tenant_42, tenant_99, etc.)
            dbOptions.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();

            // Downgrade PendingModelChangesWarning from Throw → Log (same as MasterDbContext above).
            dbOptions.ConfigureWarnings(w =>
                w.Ignore(RelationalEventId.PendingModelChangesWarning));
        });
    }

    /// <summary>
    /// Configures Npgsql for a DbContext using the shared NpgsqlDataSource.
    /// The data source handles both Azure AD token auth and password auth transparently.
    /// Centralizes shared Npgsql options (migrations assembly, retry policy).
    /// </summary>
    /// <param name="dbOptions">EF Core DbContext options builder.</param>
    /// <param name="dataSource">Shared NpgsqlDataSource (always non-null — built for either auth mode).</param>
    /// <param name="databaseOptions">Resolved database options — supplies the retry policy instead of hardcoded values.</param>
    private static void ConfigureNpgsql(
        DbContextOptionsBuilder dbOptions,
        NpgsqlDataSource dataSource,
        DatabaseOptions databaseOptions)
    {
        dbOptions.UseNpgsql(dataSource, b =>
        {
            b.MigrationsAssembly("Fakvio.Infrastructure");
            b.EnableRetryOnFailure(
                maxRetryCount: databaseOptions.MaxRetryCount,
                maxRetryDelay: TimeSpan.FromSeconds(databaseOptions.MaxRetryDelaySeconds),
                errorCodesToAdd: null);
        });

        // ConnectionError is logged by EF Core at Error level for EVERY failed attempt —
        // including the ones EnableRetryOnFailure above is about to swallow. That turned every
        // transient hiccup into a stream of "An error occurred using the connection to
        // database …" errors that looked like an outage.
        //
        // Warning, not Ignore: a genuinely unreachable database still shows up here, and when
        // the retries are exhausted the exception surfaces to the caller and is logged as an
        // error by our own code anyway.
        dbOptions.ConfigureWarnings(w =>
            w.Log((RelationalEventId.ConnectionError, LogLevel.Warning)));
    }
}
