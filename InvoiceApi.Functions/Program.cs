// ============================================================================
// Azure Functions Isolated Worker — Program.cs (Timer Triggers Only)
//
// This Functions project hosts ONLY timer triggers for background log management:
// - LogFlush: drains DatabaseLoggerProvider queue to AppLog table (every 5 seconds)
// - LogCleanup: deletes old Debug/Info logs from AppLog table (every hour)
//
// HTTP API endpoints are hosted by InvoiceApi.API on Azure App Service.
// Azure Functions is NOT suitable for hosting MVC controllers — the ASP.NET Core
// Integration does not support MVC endpoint routing (MapControllers).
// See: https://learn.microsoft.com/en-us/azure/azure-functions/dotnet-isolated-process-guide
//
// Dependencies:
// - InvoiceApi.Infrastructure: DatabaseLoggerProvider (log queue access)
// - InvoiceApi.Domain: AppLog entity
// - Microsoft.Data.SqlClient: raw ADO.NET for log writes (avoids EF circular logging)
// ============================================================================

using System.Globalization;
using InvoiceApi.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Set invariant culture globally so that decimal values are formatted consistently
// regardless of server locale (e.g., "21.0" instead of "21,0").
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

var host = new HostBuilder()
    // ConfigureFunctionsWorkerDefaults — standard worker setup for non-HTTP triggers.
    // No ASP.NET Core integration needed (no HTTP endpoints hosted here).
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        // Register DatabaseLoggerProvider — structured logging to AppLog table.
        // The provider queues log entries in a static ConcurrentQueue.
        // The LogFlush timer trigger (TimerFunctions.cs) drains this queue every 5 seconds
        // and batch-inserts entries to the AppLog table via raw ADO.NET.
        services.AddSingleton<ILoggerProvider>(new DatabaseLoggerProvider(LogLevel.Information));
    })
    .Build();

await host.RunAsync();
