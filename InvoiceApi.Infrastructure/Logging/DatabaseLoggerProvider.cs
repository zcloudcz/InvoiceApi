using System.Collections.Concurrent;
using InvoiceApi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Logging;

/// <summary>
/// Custom ILoggerProvider that writes log entries to a ConcurrentQueue for async database persistence.
///
/// How it works:
/// 1. ASP.NET Core logging calls DatabaseLogger.Log() on any thread (request handling, background services, etc.)
/// 2. Log entries are enqueued to a static ConcurrentQueue — zero allocations, non-blocking, ~nanosecond latency
/// 3. LogFlushService (BackgroundService) drains the queue every 5 seconds and batch-inserts to the AppLog table
///
/// Why this design?
/// - Direct DB writes in Log() would block request threads (MailKit, EF queries already do logging)
/// - Using a queue + background flush ensures the main pipeline is never slowed by logging I/O
/// - ConcurrentQueue is lock-free and safe for high-throughput multi-threaded access
///
/// Category filtering:
/// - "Microsoft.EntityFrameworkCore" and "Microsoft.AspNetCore" are filtered to Warning+ (they are very noisy)
/// - All other categories log at the default minimum level
/// </summary>
public class DatabaseLoggerProvider : ILoggerProvider
{
    /// <summary>
    /// Static queue shared by all DatabaseLogger instances.
    /// LogFlushService drains this queue periodically.
    /// </summary>
    internal static readonly ConcurrentQueue<AppLog> LogQueue = new();

    private readonly LogLevel _minimumLevel;

    /// <summary>
    /// Creates a new database logger provider.
    /// </summary>
    /// <param name="minimumLevel">Minimum log level to capture (default: Information)</param>
    public DatabaseLoggerProvider(LogLevel minimumLevel = LogLevel.Information)
    {
        _minimumLevel = minimumLevel;
    }

    /// <summary>
    /// Creates a logger for the specified category (typically the fully-qualified class name).
    /// </summary>
    public ILogger CreateLogger(string categoryName)
    {
        return new DatabaseLogger(categoryName, _minimumLevel);
    }

    public void Dispose()
    {
        // Nothing to dispose — the queue is static and drained by LogFlushService
        GC.SuppressFinalize(this);
    }
}
