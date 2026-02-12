using InvoiceApi.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Logging;

/// <summary>
/// Logger implementation that enqueues log entries to a ConcurrentQueue for async database persistence.
///
/// This logger is created by DatabaseLoggerProvider for each logging category.
/// It filters noisy framework categories (EF Core, ASP.NET) to Warning+ to avoid flooding the DB.
///
/// Thread-safe: ConcurrentQueue.Enqueue is lock-free.
/// Non-blocking: no I/O happens in Log() — just a memory enqueue.
/// </summary>
public class DatabaseLogger : ILogger
{
    private readonly string _categoryName;
    private readonly LogLevel _minimumLevel;

    /// <summary>
    /// Framework category prefixes that are filtered to Warning+ to reduce noise.
    /// Without this filter, EF Core alone generates hundreds of Debug/Information logs per request.
    /// </summary>
    private static readonly string[] NoisyCategories =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Hosting",
        "System.Net.Http"
    ];

    public DatabaseLogger(string categoryName, LogLevel minimumLevel)
    {
        _categoryName = categoryName;
        _minimumLevel = minimumLevel;
    }

    /// <summary>
    /// Checks if the given log level is enabled for this category.
    /// Noisy framework categories are bumped to Warning minimum.
    /// </summary>
    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel < _minimumLevel)
            return false;

        // For noisy framework categories, only log Warning and above
        if (IsNoisyCategory() && logLevel < LogLevel.Warning)
            return false;

        return true;
    }

    /// <summary>
    /// Logs a message by enqueuing an AppLog entry to the static queue.
    /// LogFlushService will pick it up and persist it to the database.
    /// </summary>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var logEntry = new AppLog
        {
            Timestamp = DateTime.UtcNow,
            Level = logLevel.ToString(),
            Source = _categoryName,
            Message = formatter(state, exception),
            Exception = exception?.ToString()
        };

        // Enqueue for async batch insert — non-blocking, zero latency impact
        DatabaseLoggerProvider.LogQueue.Enqueue(logEntry);
    }

    /// <summary>
    /// Scopes are not supported by this logger (they add complexity for minimal benefit in DB logging).
    /// Returns a no-op disposable.
    /// </summary>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <summary>
    /// Checks if this logger's category is one of the noisy framework categories.
    /// </summary>
    private bool IsNoisyCategory()
    {
        return NoisyCategories.Any(prefix => _categoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
