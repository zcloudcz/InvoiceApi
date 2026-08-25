// ============================================================================
// Azure Functions Isolated Worker — Program.cs
//
// This Functions project hosts:
// - Timer triggers: LogFlush (5s) + LogCleanup (hourly) for background log management
// - HTTP triggers: Auto-generated wrappers for all API controller actions
//   (created by Fakvio.Functions.Generator source generator at compile time)
//
// The HTTP triggers use ASP.NET Core integration (ConfigureFunctionsWebApplication)
// which provides real HttpRequest/IActionResult support. Each controller action
// becomes a separate [Function] + [HttpTrigger] — no MVC routing needed.
//
// Dependencies:
// - Fakvio.API: Controller classes (injected via DI, called directly)
// - Fakvio.Infrastructure: Shared DI registrations (DbContexts, services, auth)
// - Microsoft.Data.SqlClient: Raw ADO.NET for timer-triggered log writes
// ============================================================================

using System.Globalization;
using Microsoft.Azure.Functions.Worker;
using Fakvio.Application.Service;
using Fakvio.Functions.Middleware;
using Fakvio.Functions.Tailscale;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.DependencyInjection;
using Fakvio.Functions.Telemetry;
using Fakvio.Infrastructure.Logging;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Set invariant culture globally so that decimal values are formatted consistently
// regardless of server locale (e.g., "21.0" instead of "21,0").
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

// Npgsql 10.x strictly requires DateTimeKind.Utc for "timestamp with time zone" columns.
// Blazor WASM date pickers and JSON deserialization produce DateTime with Kind=Local or Unspecified,
// which Npgsql rejects with ArgumentException. This switch restores the pre-10.x behavior.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var host = new HostBuilder()
    // ConfigureFunctionsWebApplication — enables ASP.NET Core integration for HTTP triggers.
    // This gives generated functions access to HttpRequest, IActionResult, HttpContext, etc.
    // NOTE: This does NOT enable MVC routing (MapControllers) — each function handles its own route.
    .ConfigureFunctionsWebApplication(app =>
    {
        // ── Global Exception Handler ─────────────────────────────────────────────
        // MUST be the VERY FIRST middleware — wraps the entire pipeline.
        // Catches unhandled exceptions from ALL subsequent middleware (CORS, JWT,
        // tenant, etc.) and function code, then logs them via ILogger → AppLog DB.
        // Without this, unhandled exceptions go ONLY to App Insights and never
        // appear in the application's AppLog table.
        app.UseMiddleware<GlobalExceptionMiddleware>();

        // ── CorrelationId Middleware ──────────────────────────────────────────────
        // MUST be second — before CORS, JWT, etc.
        // Reads X-Correlation-Id from incoming request (or generates a new GUID),
        // stores it in HttpContext.Items and DatabaseLoggerProvider.CurrentCorrelationId,
        // and adds it to the response headers for client-side debugging.
        app.UseMiddleware<CorrelationIdMiddleware>();

        // ── CORS Middleware ──────────────────────────────────────────────────────
        // Must be registered early in the pipeline so CORS headers are added to
        // every response, including error responses. This middleware reads allowed
        // origins from CorsSettings:AllowedOrigins configuration and adds
        // Access-Control-Allow-* headers for matching origins.
        // Works together with CorsFunctions.cs (catch-all OPTIONS preflight handler).
        app.UseMiddleware<CorsMiddleware>();

        // ── JWT Authentication Middleware ─────────────────────────────────────────
        // CRITICAL: AddAuthentication() + AddJwtBearer() only REGISTER the services.
        // Azure Functions Isolated Worker does NOT call UseAuthentication()/UseAuthorization()
        // automatically — those are ASP.NET Core pipeline methods not available here.
        // This custom middleware reads the Authorization header, validates the JWT token,
        // and populates HttpContext.User with the authenticated ClaimsPrincipal.
        // Without this, HttpContext.User stays anonymous → all auth checks return 401.
        app.UseMiddleware<JwtAuthenticationMiddleware>();

        // ── API Key Authentication Middleware ─────────────────────────────────────
        // Mirror of the API host's ApiKey authentication scheme (registered by
        // AddFakvioAuthentication but never executed here — the isolated worker has no
        // UseAuthentication()). Handles "Authorization: Bearer fak_…", which the JWT
        // middleware above deliberately ignores, and enforces the key's read/write scope.
        //
        // Runs AFTER the JWT middleware, not before: that one wires IHttpContextAccessor
        // into the worker scope, which the authenticator's MasterDbContext needs for its
        // audit stamping. The two never fight over HttpContext.User — each handles a token
        // shape the other skips.
        app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

        // ── Impersonation Middleware ────────────────────────────────────────────────
        // MUST run AFTER JwtAuthenticationMiddleware (needs User.Claims populated)
        // and BEFORE TenantContextMiddleware (which reads the CompanyId claim).
        // When SysAdmin sends X-Company-Id header, adds CompanyId claim to identity
        // so tenant resolution picks it up automatically.
        // Without this, SysAdmin requests have no CompanyId → no Schema → crash.
        app.UseMiddleware<ImpersonationMiddleware>();

        // ── Tenant Context Middleware ──────────────────────────────────────────────
        // MUST run AFTER ImpersonationMiddleware (needs CompanyId claim set).
        // Validates tenant (provisioned? active?), sets TenantDbContext.Schema,
        // and lazily applies pending migrations.
        // Returns 403 if SysAdmin tries tenant endpoint without impersonation.
        app.UseMiddleware<TenantContextMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        // ── Application Insights ─────────────────────────────────────────────────
        // Sends structured telemetry (logs, requests, exceptions, dependency tracking)
        // to Azure Monitor. Reads APPLICATIONINSIGHTS_CONNECTION_STRING from Azure
        // App Settings automatically — no connection string needed in code.
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // ── CorrelationId enrichment for Application Insights ─────────────────
        // Adds our custom CorrelationId as a property on every telemetry item
        // (requests, traces, exceptions, dependencies). This lets us search for
        // CorrelationId in App Insights KQL: customDimensions.CorrelationId == "guid"
        services.AddHttpContextAccessor();
        services.AddSingleton<ITelemetryInitializer, CorrelationIdTelemetryInitializer>();

        // ── Shared DI registrations (same as API project) ────────────────────────
        // Registers: DbContexts, application services, cloud storage, logging, etc.
        services.AddFakvioCore(context.Configuration);

        // Registers: JWT Bearer authentication, OAuth providers (Google, Microsoft, etc.)
        // This enables HttpContext.User to have proper claims in the generated functions.
        services.AddFakvioAuthentication(context.Configuration);

        // ── Register controllers for DI injection ────────────────────────────────
        // AddControllersAsServices() registers all controller types in the DI container
        // so the generated function classes can receive them via constructor injection.
        // IMPORTANT: AddControllers() only scans the ENTRY assembly (Fakvio.Functions)
        // by default. The actual controllers live in Fakvio.API, so we must explicitly
        // add that assembly as an application part — otherwise DI can't resolve them.
        services.AddControllers()
            .AddApplicationPart(typeof(Fakvio.API.Controller.CurrencyController).Assembly)
            .AddControllersAsServices();

        // ── DatabaseLoggerProvider — structured logging to AppLog table ───────────
        // The provider queues log entries in a static ConcurrentQueue.
        // The LogFlush timer trigger (TimerFunctions.cs) drains this queue every 5 seconds.
        services.AddSingleton<ILoggerProvider>(new DatabaseLoggerProvider(LogLevel.Information));
    })
    .Build();

// Startup breadcrumb: which database auth mode won, and from which config key. Same line as
// the API host (LogDatabaseAuthMode), logged before any database work — when the database is
// down, /api/diagnostic/health cannot answer (SysAdmin login needs the master DB), so this is
// the only place the mode can be read. See SELFHOST-DB.md §3.4.
host.Services.LogDatabaseAuthMode();

// ── Tailscale tunnel (test environment) ───────────────────────────────────
// The test database sits behind Tailscale and its port is not on the public internet, so the
// connection string points at a loopback port that only exists once the tunnel is up.
//
// WHY here and not in an IHostedService: hosted services do not start until RunAsync() below,
// which is *after* the migration block — the very first socket the app opens. The tunnel has to
// exist before that. And it has to be after Build(), because that is where the real ILogger and
// IHostApplicationLifetime come from.
//
// Without TAILSCALE_AUTHKEY this is a single log line and nothing else, so local development and
// the production host (which reach their database directly) are unaffected.
var tunnelLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Fakvio.Functions.Tailscale");
try
{
    await TailscaleTunnel.StartIfConfiguredAsync(
        Environment.GetEnvironmentVariable(TailscaleTunnel.AuthKeyEnv),
        host.Services.GetRequiredService<IHostApplicationLifetime>(),
        tunnelLogger);
}
catch (Exception ex)
{
    // Do not crash the host: timer triggers and the health endpoint should still answer so the
    // failure is diagnosable. The migration below will fail too and say the same thing.
    tunnelLogger.LogError(ex, "Startup: Tailscale tunnel failed — database unreachable until resolved");
}

// ── Startup database migration (master DB only) ───────────────────────────
// Step 1: Migrate master DB (Users, Companies, SystemSettings, code tables).
// Step 2: Tenant migrations are handled LAZILY by ITenantDbContextFactory.EnsureMigratedAsync
//         — each tenant schema is migrated on first request (cached per process lifetime).
//         This is faster at startup and handles tenants provisioned while the app is running.
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        // Master DB — must be migrated first (it contains company records
        // that tell us which tenant databases exist)
        var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        logger.LogInformation("Startup: applying master database migrations...");
        await masterDb.Database.MigrateAsync();
        logger.LogInformation("Startup: master database migrated successfully");
    }
    catch (Exception ex)
    {
        // Log but don't crash — timer triggers (log flush) should still work
        // even if the database isn't ready yet.
        logger.LogError(ex, "Startup: database migration failed — API calls will return errors until resolved");
    }
}

await host.RunAsync();
