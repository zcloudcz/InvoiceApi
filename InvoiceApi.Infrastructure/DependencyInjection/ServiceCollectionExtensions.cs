using AresService;
using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Logging;
using InvoiceApi.Infrastructure.Repository;
using InvoiceApi.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZMapper;

namespace InvoiceApi.Infrastructure.DependencyInjection;

/// <summary>
/// Shared service registrations used by both InvoiceApi.API and InvoiceApi.Functions.
/// This eliminates code duplication between the two hosting models (Kestrel vs Azure Functions).
///
/// Registers:
/// - MasterDbContext + TenantDbContext (multi-tenant SQL Server)
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
    /// Registers all shared infrastructure services needed by the InvoiceApi application.
    /// Call this from both API and Functions Program.cs to keep DI registrations in sync.
    ///
    /// Example usage in API:
    ///   builder.Services.AddInvoiceApiCore(builder.Configuration);
    ///
    /// Example usage in Functions:
    ///   services.AddInvoiceApiCore(config);
    /// </summary>
    /// <param name="services">The DI container to register services into.</param>
    /// <param name="configuration">Application configuration (appsettings / local.settings).</param>
    /// <returns>The same IServiceCollection for chaining.</returns>
    public static IServiceCollection AddInvoiceApiCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // ── Database Configuration ──────────────────────────────────────────
        // Multi-tenant architecture with two DbContexts:
        // - MasterDbContext: shared DB for Users, Companies, CompanySystemSettings, code tables
        // - TenantDbContext: per-tenant DB for invoices, clients, templates, sequences

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
        services.AddScoped<ISystemConfigurationService, SystemConfigurationService>();

        // Tenant provisioning — creates, migrates, activates/deactivates tenant databases.
        // SysAdmin uses this through CompanyController to manage tenant lifecycle.
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        // Azure SQL Service — manages Azure SQL databases via ARM API (create, list, delete).
        // Used by AzureOperationController for Azure-hosted tenant provisioning with free tier support.
        services.AddScoped<IAzureSqlService, AzureSqlService>();

        // ── Cloud Storage ───────────────────────────────────────────────────
        // Google Drive and OneDrive registered as IExternalCloudStorage.
        // CloudStorageOrchestrator iterates IEnumerable<IExternalCloudStorage>
        // and routes to the correct provider based on CompanySystemSettings.
        services.AddHttpClient<InvoiceApi.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>();
        services.AddScoped<IExternalCloudStorage>(sp =>
            sp.GetRequiredService<InvoiceApi.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>());
        services.AddHttpClient<InvoiceApi.Infrastructure.Service.CloudStorage.OneDriveStorageService>();
        services.AddScoped<IExternalCloudStorage>(sp =>
            sp.GetRequiredService<InvoiceApi.Infrastructure.Service.CloudStorage.OneDriveStorageService>());
        services.AddScoped<ICloudStorageOrchestrator,
            InvoiceApi.Infrastructure.Service.CloudStorage.CloudStorageOrchestrator>();

        // ── Database Logging ────────────────────────────────────────────────
        // Structured logging to AppLog table in master DB.
        // Uses ConcurrentQueue for non-blocking enqueue; flushed by:
        // - API: LogFlushService (IHostedService, every 5 seconds)
        // - Functions: LogFlush timer trigger (every 5 seconds)
        services.AddSingleton<ILoggerProvider>(new DatabaseLoggerProvider(LogLevel.Information));

        return services;
    }

    /// <summary>
    /// Registers MasterDbContext and TenantDbContext with SQL Server providers.
    /// MasterDbContext uses a fixed connection string; TenantDbContext resolves
    /// its connection dynamically per-request based on the user's CompanyId claim.
    /// </summary>
    private static void AddDatabaseContexts(
        IServiceCollection services,
        IConfiguration configuration)
    {
        // MasterDbContext — shared database for Users, Companies, CompanySystemSettings, and code tables.
        // EnableRetryOnFailure handles transient SQL Server/Azure SQL errors (network blips,
        // connection pool exhaustion, Azure failovers) by automatically retrying failed operations.
        services.AddDbContext<MasterDbContext>(options =>
        {
            options.UseSqlServer(
                configuration.GetConnectionString("MasterConnection")
                    ?? throw new InvalidOperationException(
                        "Database connection string 'MasterConnection' not configured. " +
                        "Set it in appsettings.json, local.settings.json, or environment variable " +
                        "ConnectionStrings__MasterConnection."),
                b =>
                {
                    b.MigrationsAssembly("InvoiceApi.Infrastructure");
                    b.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null);
                });

            // EF Core 10 throws PendingModelChangesWarning by default when the current model
            // doesn't exactly match the latest migration snapshot. This blocks MigrateAsync()
            // even if the differences are cosmetic (e.g., environment-specific metadata).
            // Downgrade from Throw → Log so migrations proceed and the warning is still visible.
            options.ConfigureWarnings(w =>
                w.Log(RelationalEventId.PendingModelChangesWarning));
        });

        // TenantDbContext — per-tenant database for invoices, clients, templates, etc.
        // Registered as scoped; the actual connection string is resolved DYNAMICALLY per request.
        // 1. If the user has a CompanyId claim → look up CompanySystemSettings in master DB
        //    → build the correct tenant connection string (either custom or template-based).
        // 2. If no CompanyId (startup, migrations, EF CLI tools) → fall back to TenantTemplateConnection.
        // This ensures each request connects to the CORRECT tenant database, not the template.
        services.AddDbContext<TenantDbContext>((serviceProvider, options) =>
        {
            // Try to resolve tenant connection from the current HTTP request's CompanyId claim.
            // IHttpContextAccessor is a singleton reading from AsyncLocal — safe to resolve here.
            var httpContextAccessor = serviceProvider
                .GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            var companyIdClaim = httpContextAccessor?.HttpContext?.User?.FindFirst("CompanyId")?.Value;

            if (long.TryParse(companyIdClaim, out var companyId))
            {
                // We have a CompanyId — look up the tenant's database settings in master DB.
                // MasterDbContext is scoped, so this creates a short-lived instance for the lookup.
                var masterDb = serviceProvider.GetRequiredService<MasterDbContext>();
                var settings = masterDb.CompanySystemSettings
                    .AsNoTracking()
                    .FirstOrDefault(s => s.CompanyId == companyId);

                if (settings is { IsProvisioned: true, IsActive: true })
                {
                    // Build the connection string for this specific tenant.
                    // Strategy 1: Use custom connection string if set (e.g., different SQL Server).
                    // Strategy 2: Take the master connection string and swap the database name.
                    string tenantConnString;

                    if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
                    {
                        tenantConnString = settings.ConnectionString;
                    }
                    else
                    {
                        var masterConn = configuration.GetConnectionString("MasterConnection")!;
                        var connBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(masterConn)
                        {
                            InitialCatalog = settings.DatabaseName
                        };
                        tenantConnString = connBuilder.ConnectionString;
                    }

                    options.UseSqlServer(tenantConnString, b =>
                    {
                        b.MigrationsAssembly("InvoiceApi.Infrastructure");
                        b.EnableRetryOnFailure(
                            maxRetryCount: 3,
                            maxRetryDelay: TimeSpan.FromSeconds(5),
                            errorNumbersToAdd: null);
                    });

                    // Downgrade PendingModelChangesWarning from Throw → Log (same as MasterDbContext above).
                    options.ConfigureWarnings(w =>
                        w.Log(RelationalEventId.PendingModelChangesWarning));
                    return;
                }
            }

            // Fallback: no CompanyId available or tenant not provisioned/active.
            // This happens during:
            // - Application startup (migrations, seed data)
            // - EF CLI tools (dotnet ef migrations add/update)
            // - Background jobs / timer triggers without HTTP context
            // The template connection is only used for migration generation, NOT for runtime queries.
            var templateConn = configuration.GetConnectionString("TenantTemplateConnection")
                ?? throw new InvalidOperationException(
                    "Database connection string 'TenantTemplateConnection' not configured.");
            options.UseSqlServer(templateConn, b =>
            {
                b.MigrationsAssembly("InvoiceApi.Infrastructure");
                b.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
            });

            // Downgrade PendingModelChangesWarning from Throw → Log (same as MasterDbContext above).
            options.ConfigureWarnings(w =>
                w.Log(RelationalEventId.PendingModelChangesWarning));
        });
    }
}
