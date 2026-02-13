// ============================================================================
// Azure Functions Isolated Worker — Program.cs
//
// This file replicates the DI registrations from InvoiceApi.API/Program.cs
// adapted for Azure Functions v4 Isolated Worker Model with ASP.NET Core
// Integration. The original API project is NOT modified.
//
// Key differences from API Program.cs:
// - No WebApplication.CreateBuilder() → uses HostBuilder + ConfigureFunctionsWebApplication()
// - No Swagger/OpenAPI (not needed in Azure Functions — use API Management instead)
// - No CORS (handled by Azure Functions host / Azure API Management)
// - No auto-migration on startup (handled by deployment pipeline or MigrationTool)
// - No AddHostedService (replaced by timer trigger functions in TimerFunctions.cs)
// - AddApplicationPart() discovers controllers from the API assembly
//
// IMPORTANT: When DI registrations change in API/Program.cs, this file must be
// updated manually to stay in sync. This duplication is intentional to preserve
// complete hosting-model independence (the original API must not change).
// ============================================================================

using System.Globalization;
using System.Security.Claims;
using System.Text;
using AresService;
using InvoiceApi.API.Controller;
using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Authentication;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Logging;
using InvoiceApi.Infrastructure.Mapping;
using InvoiceApi.Infrastructure.Repository;
using InvoiceApi.Infrastructure.Service;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ZMapper;

// Set invariant culture globally so that decimal values are formatted consistently
// regardless of server locale (e.g., "21.0" instead of "21,0").
// Without this, Czech locale expects comma separator and throws FormatException.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var host = new HostBuilder()
    // ConfigureFunctionsWebApplication enables ASP.NET Core integration for HTTP triggers.
    // This allows Azure Functions to use HttpRequest/IActionResult and host the full
    // ASP.NET Core middleware pipeline (authentication, routing, controllers).
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        var config = context.Configuration;

        // ── Database (same as API lines 33–111) ─────────────────────────────
        // MasterDbContext — shared database for Users, Companies, CompanySystemSettings, and code tables.
        // EnableRetryOnFailure handles transient SQL Server/Azure SQL errors automatically.
        services.AddDbContext<MasterDbContext>(options =>
            options.UseSqlServer(
                config.GetConnectionString("MasterConnection")
                    ?? throw new InvalidOperationException(
                        "Database connection string 'MasterConnection' not configured. " +
                        "Set it in local.settings.json (ConnectionStrings:MasterConnection) " +
                        "or via environment variable ConnectionStrings__MasterConnection."),
                b =>
                {
                    b.MigrationsAssembly("InvoiceApi.Infrastructure");
                    b.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null);
                }));

        // TenantDbContext — per-tenant database for invoices, clients, templates, etc.
        // Registered as scoped; the actual connection string is resolved DYNAMICALLY per request.
        // 1. If the user has a CompanyId claim → look up CompanySystemSettings in master DB
        //    → build the correct tenant connection string (either custom or template-based).
        // 2. If no CompanyId (startup, EF CLI tools) → fall back to TenantTemplateConnection.
        services.AddDbContext<TenantDbContext>((serviceProvider, options) =>
        {
            // Try to resolve tenant connection from the current HTTP request's CompanyId claim.
            var httpContextAccessor = serviceProvider.GetService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
            var companyIdClaim = httpContextAccessor?.HttpContext?.User?.FindFirst("CompanyId")?.Value;

            if (long.TryParse(companyIdClaim, out var companyId))
            {
                // We have a CompanyId — look up the tenant's database settings in master DB.
                var masterDb = serviceProvider.GetRequiredService<MasterDbContext>();
                var settings = masterDb.CompanySystemSettings
                    .AsNoTracking()
                    .FirstOrDefault(s => s.CompanyId == companyId);

                if (settings is { IsProvisioned: true, IsActive: true })
                {
                    // Build the connection string for this specific tenant.
                    // Strategy 1: Use custom connection string if set.
                    // Strategy 2: Take the master connection string and swap the database name.
                    string tenantConnString;

                    if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
                    {
                        tenantConnString = settings.ConnectionString;
                    }
                    else
                    {
                        var masterConn = config.GetConnectionString("MasterConnection")!;
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
                    return;
                }
            }

            // Fallback: no CompanyId available or tenant not provisioned/active.
            // This happens during startup, EF CLI tools, or timer trigger functions.
            var templateConn = config.GetConnectionString("TenantTemplateConnection")
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
        });

        // ── Core services (same as API lines 113–175) ───────────────────────

        // Register HttpClient for ARES service (Czech business registry)
        services.AddHttpClient<IAresService, AresServiceImpl>();

        // Register HttpContextAccessor — required for ICurrentUserService to read JWT claims
        services.AddHttpContextAccessor();

        // Register ICurrentUserService — reads current user ID from JWT claims for audit trail
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        // Register ITenantResolver — reads current CompanyId from JWT claims for tenant resolution
        services.AddScoped<ITenantResolver, HttpContextTenantResolver>();

        // Register ITenantDbContextFactory — creates properly-connected TenantDbContext instances
        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();

        // Register repositories
        services.AddScoped<IAresCacheRepository, AresCacheRepository>();

        // Register ZMapper — source-generated object mapping (compile-time, zero reflection)
        services.AddZMapper();

        // Register Data Protection — used by TwoFactorService for encrypting TOTP secrets
        services.AddDataProtection();

        // ── Application services (same as API lines 139–175) ────────────────

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

        // Register paylibo.com REST client for Czech QR Platba generation
        services.AddHttpClient<IPayliboClient, PayliboClient>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IContentTemplateService, ContentTemplateService>();
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<ISystemConfigurationService, SystemConfigurationService>();

        // Register tenant provisioning service — creates, migrates, activates/deactivates tenant databases
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        // ── Cloud storage services (same as API lines 169–175) ──────────────
        // Google Drive and OneDrive implementations registered as IExternalCloudStorage
        services.AddHttpClient<InvoiceApi.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>();
        services.AddScoped<IExternalCloudStorage>(sp =>
            sp.GetRequiredService<InvoiceApi.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>());
        services.AddHttpClient<InvoiceApi.Infrastructure.Service.CloudStorage.OneDriveStorageService>();
        services.AddScoped<IExternalCloudStorage>(sp =>
            sp.GetRequiredService<InvoiceApi.Infrastructure.Service.CloudStorage.OneDriveStorageService>());
        services.AddScoped<ICloudStorageOrchestrator,
            InvoiceApi.Infrastructure.Service.CloudStorage.CloudStorageOrchestrator>();

        // ── Database logging ─────────────────────────────────────────────────
        // Register the DatabaseLoggerProvider for structured logging to AppLog table.
        // NOTE: LogFlushService and LogCleanupService are NOT registered here because Azure
        // Functions doesn't support IHostedService reliably. Instead, timer trigger functions
        // in TimerFunctions.cs handle log flushing (every 5s) and cleanup (every 1h).
        services.AddSingleton<ILoggerProvider>(new DatabaseLoggerProvider(LogLevel.Information));

        // ── Controllers from API assembly ────────────────────────────────────
        // AddApplicationPart discovers all controllers defined in InvoiceApi.API
        // so they can be routed through the Azure Functions HTTP trigger pipeline.
        services.AddControllers()
            .AddApplicationPart(typeof(AuthController).Assembly);

        // ── JWT Authentication (same as API lines 186–266) ──────────────────
        var jwtSecret = config["JwtSettings:Secret"]
            ?? throw new InvalidOperationException(
                "JWT Secret not configured. Set it in local.settings.json (JwtSettings:Secret) " +
                "or via environment variable JwtSettings__Secret.");
        var jwtIssuer = config["JwtSettings:Issuer"] ?? "InvoiceApi";
        var jwtAudience = config["JwtSettings:Audience"] ?? "InvoiceApiClient";

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtIssuer,
                ValidAudience = jwtAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                ClockSkew = TimeSpan.Zero // Remove default 5 minute clock skew
            };
        });

        // ── External OAuth Providers (conditional) ──────────────────────────
        // Each provider is only registered when its ClientId/AppId is configured.
        // Empty credentials cause OAuthOptions.Validate() to throw on every request.
        var googleClientId = config["OAuth:Google:ClientId"];
        if (!string.IsNullOrEmpty(googleClientId))
        {
            authBuilder.AddGoogle("Google", o =>
            {
                o.ClientId = googleClientId;
                o.ClientSecret = config["OAuth:Google:ClientSecret"] ?? "";
                o.CallbackPath = "/api/auth/google-callback";
            });
        }

        var microsoftClientId = config["OAuth:Microsoft:ClientId"];
        if (!string.IsNullOrEmpty(microsoftClientId))
        {
            authBuilder.AddMicrosoftAccount("Microsoft", o =>
            {
                o.ClientId = microsoftClientId;
                o.ClientSecret = config["OAuth:Microsoft:ClientSecret"] ?? "";
                o.CallbackPath = "/api/auth/microsoft-callback";
            });
        }

        var facebookAppId = config["OAuth:Facebook:AppId"];
        if (!string.IsNullOrEmpty(facebookAppId))
        {
            authBuilder.AddFacebook("Facebook", o =>
            {
                o.AppId = facebookAppId;
                o.AppSecret = config["OAuth:Facebook:AppSecret"] ?? "";
                o.CallbackPath = "/api/auth/facebook-callback";
            });
        }

        // Seznam.cz — custom OAuth handler (Czech-specific provider)
        var seznamClientId = config["OAuth:Seznam:ClientId"];
        if (!string.IsNullOrEmpty(seznamClientId))
        {
            authBuilder.AddSeznam("Seznam", o =>
            {
                o.ClientId = seznamClientId;
                o.ClientSecret = config["OAuth:Seznam:ClientSecret"] ?? "";
                o.CallbackPath = "/api/auth/seznam-callback";
            });
        }

        services.AddAuthorization();
    })
    .Build();

await host.RunAsync();
