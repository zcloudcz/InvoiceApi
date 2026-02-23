// ============================================================================
// Azure Functions Isolated Worker — Program.cs
//
// This Functions project hosts:
// - Timer triggers: LogFlush (5s) + LogCleanup (hourly) for background log management
// - HTTP triggers: Auto-generated wrappers for all API controller actions
//   (created by InvoiceApi.Functions.Generator source generator at compile time)
//
// The HTTP triggers use ASP.NET Core integration (ConfigureFunctionsWebApplication)
// which provides real HttpRequest/IActionResult support. Each controller action
// becomes a separate [Function] + [HttpTrigger] — no MVC routing needed.
//
// Dependencies:
// - InvoiceApi.API: Controller classes (injected via DI, called directly)
// - InvoiceApi.Infrastructure: Shared DI registrations (DbContexts, services, auth)
// - Microsoft.Data.SqlClient: Raw ADO.NET for timer-triggered log writes
// ============================================================================

using System.Globalization;
using InvoiceApi.Infrastructure.DependencyInjection;
using InvoiceApi.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Set invariant culture globally so that decimal values are formatted consistently
// regardless of server locale (e.g., "21.0" instead of "21,0").
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var host = new HostBuilder()
    // ConfigureFunctionsWebApplication — enables ASP.NET Core integration for HTTP triggers.
    // This gives generated functions access to HttpRequest, IActionResult, HttpContext, etc.
    // NOTE: This does NOT enable MVC routing (MapControllers) — each function handles its own route.
    .ConfigureFunctionsWebApplication()
    .ConfigureServices((context, services) =>
    {
        // ── Shared DI registrations (same as API project) ────────────────────────
        // Registers: DbContexts, application services, cloud storage, logging, etc.
        services.AddInvoiceApiCore(context.Configuration);

        // Registers: JWT Bearer authentication, OAuth providers (Google, Microsoft, etc.)
        // This enables HttpContext.User to have proper claims in the generated functions.
        services.AddInvoiceApiAuthentication(context.Configuration);

        // ── Register controllers for DI injection ────────────────────────────────
        // AddControllersAsServices() registers all controller types in the DI container
        // so the generated function classes can receive them via constructor injection.
        // This is critical — without this, the controllers can't be injected.
        services.AddControllers().AddControllersAsServices();

        // ── DatabaseLoggerProvider — structured logging to AppLog table ───────────
        // The provider queues log entries in a static ConcurrentQueue.
        // The LogFlush timer trigger (TimerFunctions.cs) drains this queue every 5 seconds.
        services.AddSingleton<ILoggerProvider>(new DatabaseLoggerProvider(LogLevel.Information));
    })
    .Build();

await host.RunAsync();
