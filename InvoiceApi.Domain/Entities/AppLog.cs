namespace InvoiceApi.Domain.Entities;

/// <summary>
/// Lightweight log entry stored in the master database.
/// NOT derived from BaseEntity — logs don't need audit fields (CreatedByUserId, UpdatedAt, etc.)
/// because they are system-generated, immutable, and auto-cleaned.
///
/// Logs are written asynchronously by DatabaseLoggerProvider via a ConcurrentQueue + BackgroundService.
/// This ensures zero latency impact on the main request pipeline.
///
/// Retention:
/// - Debug/Information: auto-deleted after 48 hours (LogCleanupService)
/// - Warning/Error/Critical: kept indefinitely (until manual cleanup)
/// </summary>
public class AppLog
{
    /// <summary>
    /// Auto-generated primary key (IDENTITY column).
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// When the log entry was created (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Log severity level: "Debug", "Information", "Warning", "Error", "Critical".
    /// Indexed for efficient filtering on the Logs page.
    /// </summary>
    public string Level { get; set; } = "";

    /// <summary>
    /// Logger category name (typically the fully-qualified class name).
    /// Example: "InvoiceApi.Infrastructure.Service.EmailService"
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// The log message text. Can be long (nvarchar(max)).
    /// </summary>
    public string Message { get; set; } = "";

    /// <summary>
    /// Full exception string (Exception.ToString()) if an exception was logged.
    /// Null for non-error log entries.
    /// </summary>
    public string? Exception { get; set; }

    /// <summary>
    /// The authenticated user's ID at the time of logging, if available.
    /// Null for unauthenticated requests or background service logs.
    /// </summary>
    public long? UserId { get; set; }

    /// <summary>
    /// The company (tenant) ID at the time of logging, if available.
    /// Null for master-only operations or unauthenticated requests.
    /// </summary>
    public long? CompanyId { get; set; }

    /// <summary>
    /// The HTTP request path that triggered this log entry (e.g., "/api/invoice/5").
    /// Null for background service logs or non-HTTP contexts.
    /// </summary>
    public string? RequestPath { get; set; }
}
