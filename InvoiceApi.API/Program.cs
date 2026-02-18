using System.Globalization;
using AresService;
using InvoiceApi.API.Middleware;
using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Data;
using InvoiceApi.Infrastructure.Repository;
using InvoiceApi.Infrastructure.Service;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Text;
using InvoiceApi.Infrastructure.Authentication;
using InvoiceApi.Infrastructure.Logging;
using InvoiceApi.Infrastructure.Mapping;
using ZMapper;

// Set invariant culture globally so that decimal values are formatted consistently
// regardless of server locale (e.g., "21.0" instead of "21,0").
// Without this, Czech locale expects comma separator and throws FormatException.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container

// Database configuration - Azure SQL / SQL Server (multi-tenant architecture)

// MasterDbContext — shared database for Users, Companies, CompanySystemSettings, and code tables.
// EnableRetryOnFailure handles transient SQL Server/Azure SQL errors (network blips,
// connection pool exhaustion, Azure failovers) by automatically retrying failed operations.
builder.Services.AddDbContext<MasterDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("MasterConnection")
            ?? throw new InvalidOperationException(
                "Database connection string 'MasterConnection' not configured. " +
                "Set it in appsettings.json or appsettings.Development.json."),
        b =>
        {
            b.MigrationsAssembly("InvoiceApi.Infrastructure");
            b.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
        }));

// TenantDbContext — per-tenant database for invoices, clients, templates, etc.
// Registered as scoped; the actual connection string is resolved DYNAMICALLY per request.
// 1. If the user has a CompanyId claim → look up CompanySystemSettings in master DB
//    → build the correct tenant connection string (either custom or template-based).
// 2. If no CompanyId (startup, migrations, EF CLI tools) → fall back to TenantTemplateConnection.
// This ensures each request connects to the CORRECT tenant database, not the template.
builder.Services.AddDbContext<TenantDbContext>((serviceProvider, options) =>
{
    // Try to resolve tenant connection from the current HTTP request's CompanyId claim.
    // IHttpContextAccessor is a singleton reading from AsyncLocal — safe to resolve here.
    var httpContextAccessor = serviceProvider.GetService<IHttpContextAccessor>();
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
            // Strategy 1: Use custom connection string if set (e.g., different SQL Server instance).
            // Strategy 2: Take the master connection string and swap the database name.
            string tenantConnString;

            if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
            {
                tenantConnString = settings.ConnectionString;
            }
            else
            {
                var masterConn = builder.Configuration.GetConnectionString("MasterConnection")!;
                var connBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(masterConn)
                {
                    InitialCatalog = settings.DatabaseName
                };
                tenantConnString = connBuilder.ConnectionString;
            }

            options.UseSqlServer(tenantConnString, b =>
            {
                b.MigrationsAssembly("InvoiceApi.Infrastructure");
                b.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
            });
            return;
        }
    }

    // Fallback: no CompanyId available or tenant not provisioned/active.
    // This happens during:
    // - Application startup (migrations, seed data)
    // - EF CLI tools (dotnet ef migrations add/update)
    // - Background jobs without HTTP context
    // The template connection is only used for migration generation, NOT for runtime queries.
    var templateConn = builder.Configuration.GetConnectionString("TenantTemplateConnection")
        ?? throw new InvalidOperationException(
            "Database connection string 'TenantTemplateConnection' not configured.");
    options.UseSqlServer(templateConn, b =>
    {
        b.MigrationsAssembly("InvoiceApi.Infrastructure");
        b.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null);
    });
});

// Register HttpClient for ARES service
builder.Services.AddHttpClient<IAresService, AresServiceImpl>();

// Register HttpContextAccessor — required for ICurrentUserService to read JWT claims
builder.Services.AddHttpContextAccessor();

// Register ICurrentUserService — reads current user ID from JWT claims for audit trail
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// Register ITenantResolver — reads current CompanyId from JWT claims for tenant resolution
builder.Services.AddScoped<ITenantResolver, HttpContextTenantResolver>();

// Register ITenantDbContextFactory — creates properly-connected TenantDbContext instances
// at runtime based on CompanySystemSettings in the master database
builder.Services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();

// Register repositories
builder.Services.AddScoped<IAresCacheRepository, AresCacheRepository>();

// Register ZMapper — source-generated object mapping (compile-time, zero reflection)
builder.Services.AddZMapper();

// Register Data Protection — used by TwoFactorService for encrypting TOTP secrets and session tokens
builder.Services.AddDataProtection();

// Register application services
builder.Services.AddScoped<ITwoFactorService, TwoFactorService>();
builder.Services.AddScoped<IClientService, ClientService>();
builder.Services.AddScoped<IInvoiceService, InvoiceService>();
builder.Services.AddScoped<INumberSequenceService, NumberSequenceService>();
builder.Services.AddScoped<IVatRateService, VatRateService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ICurrencyService, CurrencyService>();
builder.Services.AddScoped<IInvoiceTemplateService, InvoiceTemplateService>();
builder.Services.AddScoped<IPdfExportService, PdfExportService>();
builder.Services.AddScoped<IQrPaymentService, QrPaymentService>();

// Register paylibo.com REST client for Czech QR Platba generation.
// Uses HttpClientFactory for proper HttpClient lifecycle management.
// The paylibo API converts Czech domestic bank account format to valid SPD QR codes.
builder.Services.AddHttpClient<IPayliboClient, PayliboClient>();
builder.Services.AddScoped<IEmailService, EmailService>();
// EmailTemplateService replaced by ContentTemplateService (registered below)
builder.Services.AddScoped<IContentTemplateService, ContentTemplateService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ISystemConfigurationService, SystemConfigurationService>();

// Register tenant provisioning service — creates, migrates, activates/deactivates tenant databases.
// SysAdmin uses this through CompanyController to manage tenant lifecycle.
builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

// Register cloud storage services — Google Drive and OneDrive implementations.
// Both are registered as IExternalCloudStorage so CloudStorageOrchestrator can iterate
// over IEnumerable<IExternalCloudStorage> and route to the correct provider.
// HttpClient is registered per concrete type, then each type is also added to IExternalCloudStorage.
builder.Services.AddHttpClient<InvoiceApi.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>();
builder.Services.AddScoped<IExternalCloudStorage>(sp =>
    sp.GetRequiredService<InvoiceApi.Infrastructure.Service.CloudStorage.GoogleDriveStorageService>());
builder.Services.AddHttpClient<InvoiceApi.Infrastructure.Service.CloudStorage.OneDriveStorageService>();
builder.Services.AddScoped<IExternalCloudStorage>(sp =>
    sp.GetRequiredService<InvoiceApi.Infrastructure.Service.CloudStorage.OneDriveStorageService>());
builder.Services.AddScoped<ICloudStorageOrchestrator, InvoiceApi.Infrastructure.Service.CloudStorage.CloudStorageOrchestrator>();

// Register database logging — writes log entries to the AppLog table in master DB.
// Uses a ConcurrentQueue for non-blocking enqueue + BackgroundService for periodic flush.
builder.Logging.AddProvider(new DatabaseLoggerProvider(LogLevel.Information));
builder.Services.AddHostedService<LogFlushService>();
builder.Services.AddHostedService<LogCleanupService>();

// Add controllers
builder.Services.AddControllers();

// Configure JWT Authentication
// SECURITY: JWT secret is NOT stored in appsettings.json (committed to Git).
// - Development: stored in appsettings.Development.json (Git-ignored) or User Secrets
// - Production: use environment variable JwtSettings__Secret or a Key Vault
var jwtSecret = builder.Configuration["JwtSettings:Secret"]
    ?? throw new InvalidOperationException(
        "JWT Secret not configured. Set it in appsettings.Development.json, " +
        "User Secrets (dotnet user-secrets set \"JwtSettings:Secret\" \"<value>\"), " +
        "or environment variable JwtSettings__Secret.");
var jwtIssuer = builder.Configuration["JwtSettings:Issuer"] ?? "InvoiceApi";
var jwtAudience = builder.Configuration["JwtSettings:Audience"] ?? "InvoiceApiClient";

// ── JWT Authentication ───────────────────────────────────────────────────
var authBuilder = builder.Services.AddAuthentication(options =>
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

// ── External OAuth Providers (conditional) ───────────────────────────────
// Each provider is only registered when its ClientId/AppId is configured.
// Empty credentials cause OAuthOptions.Validate() to throw ArgumentException,
// crashing the authentication middleware on every request — including JWT login.
var googleClientId = builder.Configuration["OAuth:Google:ClientId"];
if (!string.IsNullOrEmpty(googleClientId))
{
    authBuilder.AddGoogle("Google", o =>
    {
        o.ClientId = googleClientId;
        o.ClientSecret = builder.Configuration["OAuth:Google:ClientSecret"] ?? "";
        o.CallbackPath = "/api/auth/google-callback";
    });
}

var microsoftClientId = builder.Configuration["OAuth:Microsoft:ClientId"];
if (!string.IsNullOrEmpty(microsoftClientId))
{
    authBuilder.AddMicrosoftAccount("Microsoft", o =>
    {
        o.ClientId = microsoftClientId;
        o.ClientSecret = builder.Configuration["OAuth:Microsoft:ClientSecret"] ?? "";
        o.CallbackPath = "/api/auth/microsoft-callback";
    });
}

var facebookAppId = builder.Configuration["OAuth:Facebook:AppId"];
if (!string.IsNullOrEmpty(facebookAppId))
{
    authBuilder.AddFacebook("Facebook", o =>
    {
        o.AppId = facebookAppId;
        o.AppSecret = builder.Configuration["OAuth:Facebook:AppSecret"] ?? "";
        o.CallbackPath = "/api/auth/facebook-callback";
    });
}

// Seznam.cz — custom OAuth handler (Czech-specific provider)
var seznamClientId = builder.Configuration["OAuth:Seznam:ClientId"];
if (!string.IsNullOrEmpty(seznamClientId))
{
    authBuilder.AddSeznam("Seznam", o =>
    {
        o.ClientId = seznamClientId;
        o.ClientSecret = builder.Configuration["OAuth:Seznam:ClientSecret"] ?? "";
        o.CallbackPath = "/api/auth/seznam-callback";
    });
}

builder.Services.AddAuthorization();

// Add Swagger/OpenAPI — provides interactive API documentation at /swagger.
// Includes JWT Bearer auth support so developers can test authenticated endpoints
// directly from the Swagger UI using the "Authorize" button.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    // API metadata shown in the Swagger UI header
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Invoice API",
        Version = "v1",
        Description = "Multi-tenant invoicing API with JWT authentication. " +
                      "Use the Authorize button to paste your Bearer token."
    });

    // Include XML comments for better Swagger documentation.
    // The XML file is generated by <GenerateDocumentationFile> in the csproj.
    var xmlFiles = Directory.GetFiles(AppContext.BaseDirectory, "*.xml", SearchOption.TopDirectoryOnly);
    foreach (var xmlFile in xmlFiles)
    {
        options.IncludeXmlComments(xmlFile);
    }

    // Define the JWT Bearer security scheme.
    // This adds the "Authorize" button to Swagger UI where users can paste their token.
    // Uses SecuritySchemeType.Http with "bearer" scheme — Swagger UI will show a simple token input.
    const string bearerSchemeId = "Bearer";
    options.AddSecurityDefinition(bearerSchemeId, new OpenApiSecurityScheme
    {
        Description = "Enter your JWT token (without the 'Bearer' prefix)",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",   // lowercase per RFC 7235
        BearerFormat = "JWT"
    });

    // Apply the JWT Bearer requirement globally to all endpoints.
    // Individual endpoints can override this with [AllowAnonymous].
    // NOTE: Swashbuckle v10 / OpenAPI.NET v2 requires a Func<OpenApiDocument, ...> delegate
    // because OpenApiSecuritySchemeReference now needs the document instance.
    options.AddSecurityRequirement(document =>
        new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(bearerSchemeId, document)] = new List<string>()
        });
});

// CORS: Restrict to configured origins (no more AllowAnyOrigin).
// Origins are loaded from CorsSettings:AllowedOrigins in appsettings.json.
// Development overrides allow localhost; production requires explicit domain(s).
var allowedOrigins = builder.Configuration.GetSection("CorsSettings:AllowedOrigins").Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            // Production / Development: only allow configured origins
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .AllowCredentials(); // Required for cookie-based auth and SignalR
        }
        else
        {
            // Fallback: no origins configured — block all cross-origin requests
            policy.SetIsOriginAllowed(_ => false)
                  .AllowAnyMethod()
                  .AllowAnyHeader();
        }
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline

// Apply database migrations automatically on startup.
// Step 1: Migrate the master database (Users, Companies, CompanySystemSettings, code tables).
// Step 2: Migrate all provisioned + active tenant databases (invoices, clients, etc.).
// This ensures all schemas are up to date before handling requests.
// Skip during integration tests — InMemoryDatabase does not support migrations.
if (!app.Environment.IsEnvironment("Testing"))
{
    using (var scope = app.Services.CreateScope())
    {
        // Master DB migrations — always applied first
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

        await masterDb.Database.MigrateAsync();

        // Tenant DB migrations — applied to all active tenants.
        // Uses per-tenant error handling so one failed tenant doesn't block the others.
        var provisioningService = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        var migrated = await provisioningService.MigrateAllTenantsAsync();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Startup: migrated {Count} tenant database(s)", migrated);

        // FOR DEVELOPMENT ONLY - delete all dbs and start fresh on each run. Comment out in production!
        //await provisioningService.DeleteAllTenantDbs();
        //await masterDb.Database.EnsureDeletedAsync();
        //await masterDb.Database.MigrateAsync();
    }
}

//if (app.Environment.IsDevelopment())
//{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Invoice API v1");
        options.RoutePrefix = string.Empty; // Swagger at root URL
    });
//}

app.UseHttpsRedirection();
app.UseCors();

// Authentication must come before Authorization
app.UseAuthentication();
app.UseAuthorization();

// Impersonation middleware — allows SysAdmin to act as a specific company
// by sending X-Company-Id header. Must be after auth so we can check the role.
app.UseImpersonation();

// Tenant context middleware — validates that authenticated users have a valid,
// provisioned, and active tenant for tenant-scoped API endpoints.
// Skips master-only paths (/api/auth, /api/user, /api/company, /swagger, /health).
// Must be after UseImpersonation so the CompanyId claim is already set.
app.UseTenantContext();

app.MapControllers();

await app.RunAsync();

// Required for WebApplicationFactory<Program> in integration tests.
// The top-level statements file doesn't expose a named class by default,
// so this partial declaration makes the implicit Program class accessible.
public partial class Program { }
