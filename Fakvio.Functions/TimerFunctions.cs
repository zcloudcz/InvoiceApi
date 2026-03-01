using System.Collections.Concurrent;
using System.Reflection;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Logging;
using Microsoft.Azure.Functions.Worker;
// PostgreSQL: Using Npgsql instead of Microsoft.Data.SqlClient for PostgreSQL connectivity
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions;

/// <summary>
/// Timer trigger functions that replace the IHostedService-based background services
/// from Fakvio.Infrastructure.Logging (LogFlushService + LogCleanupService).
///
/// Azure Functions does NOT support IHostedService (BackgroundService) reliably in the
/// Isolated Worker Model. Instead, timer triggers provide the same periodic execution
/// with built-in Azure infrastructure for reliability and monitoring.
///
/// Why timer triggers instead of hosted services?
/// - Azure Functions consumption plan can scale to zero; hosted services need a running host.
/// - Timer triggers are monitored by Azure Functions runtime (execution history, failures).
/// - The Functions host manages concurrency — no risk of overlapping executions.
///
/// Both functions use raw ADO.NET (NpgsqlConnection) instead of EF Core DbContext to avoid
/// circular logging dependency (EF Core operations trigger logging → would re-enqueue → infinite loop).
/// </summary>
public class TimerFunctions
{
    private readonly string _connectionString;
    private readonly ILogger<TimerFunctions> _logger;

    /// <summary>
    /// How long to keep Debug/Information logs before auto-deletion (same as LogCleanupService).
    /// </summary>
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(48);

    /// <summary>
    /// Reference to DatabaseLoggerProvider.LogQueue obtained via reflection.
    /// The queue is declared as 'internal static' in the Infrastructure assembly, so we
    /// access it through reflection to avoid modifying the existing project.
    /// Initialized once (lazy, thread-safe) and cached for the lifetime of the process.
    /// </summary>
    private static readonly Lazy<ConcurrentQueue<AppLog>> LogQueueRef = new(() =>
    {
        // DatabaseLoggerProvider.LogQueue is 'internal static readonly ConcurrentQueue<AppLog>'
        // in Fakvio.Infrastructure.Logging. We use reflection to access it without
        // adding [InternalsVisibleTo] to the Infrastructure project (zero existing-project changes).
        var field = typeof(DatabaseLoggerProvider)
            .GetField("LogQueue", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        return (ConcurrentQueue<AppLog>)(field?.GetValue(null)
            ?? throw new InvalidOperationException(
                "Could not access DatabaseLoggerProvider.LogQueue via reflection. " +
                "Ensure the field still exists in Fakvio.Infrastructure.Logging.DatabaseLoggerProvider."));
    });

    public TimerFunctions(IConfiguration configuration, ILogger<TimerFunctions> logger)
    {
        // PostgreSQL: Changed from "MasterConnection" to "DefaultConnection" for PostgreSQL migration.
        // DefaultConnection points to the database where the AppLog table lives.
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "DefaultConnection string not configured for TimerFunctions.");
        _logger = logger;
    }

    /// <summary>
    /// Drains the DatabaseLoggerProvider.LogQueue and batch-inserts entries to the AppLog table.
    ///
    /// Runs every 5 seconds (CRON: "*/5 * * * * *" — seconds granularity).
    /// Replaces LogFlushService.FlushAsync() from Fakvio.Infrastructure.Logging.
    ///
    /// Design:
    /// - Drains ALL entries from the static ConcurrentQueue in one batch.
    /// - Uses parameterized SQL to prevent injection.
    /// - If the database is unavailable, the batch is lost (logs are best-effort).
    /// - Catches all exceptions to prevent the Functions runtime from marking the trigger as failed.
    /// </summary>
    [Function("LogFlush")]
    public async Task FlushLogs(
        [TimerTrigger("*/10 * * * * *")] TimerInfo timer)
    {
        // Drain the ConcurrentQueue into a local list (non-blocking, lock-free dequeue).
        // LogQueueRef accesses the same static queue used by DatabaseLoggerProvider across the process.
        var logQueue = LogQueueRef.Value;
        var batch = new List<AppLog>();
        while (logQueue.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        // Nothing to flush — skip the database connection entirely
        if (batch.Count == 0)
            return;

        try
        {
            // PostgreSQL: NpgsqlConnection replaces SqlConnection for PostgreSQL database access
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Insert each log entry with parameterized SQL to prevent injection.
            // Same INSERT logic as LogFlushService.FlushAsync().
            foreach (var entry in batch)
            {
                await using var cmd = connection.CreateCommand();
                // Insert log entry with all columns including CorrelationId for request tracing.
                // CorrelationId is set by CorrelationIdMiddleware via AsyncLocal → DatabaseLogger.
                // PostgreSQL: Use double-quoted identifiers instead of SQL Server bracket identifiers [Table]
                cmd.CommandText = @"
                    INSERT INTO ""AppLog"" (""Timestamp"", ""Level"", ""Source"", ""Message"", ""Exception"", ""UserId"", ""CompanyId"", ""RequestPath"", ""CorrelationId"")
                    VALUES (@timestamp, @level, @source, @message, @exception, @userId, @companyId, @requestPath, @correlationId)";

                cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp);
                cmd.Parameters.AddWithValue("@level", entry.Level);
                cmd.Parameters.AddWithValue("@source", entry.Source);
                cmd.Parameters.AddWithValue("@message", entry.Message);
                cmd.Parameters.AddWithValue("@exception", (object?)entry.Exception ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@userId", (object?)entry.UserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@companyId", (object?)entry.CompanyId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@requestPath", (object?)entry.RequestPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@correlationId", (object?)entry.CorrelationId ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            // Log to console only — we can't log to the database logger here (infinite loop!).
            // In Azure Functions, console output goes to Application Insights.
            Console.Error.WriteLine(
                $"LogFlush: Failed to flush {batch.Count} log entries: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes old Debug/Information/Trace log entries from the AppLog table.
    ///
    /// Runs every hour (CRON: "0 0 * * * *" — top of every hour).
    /// Replaces LogCleanupService from Fakvio.Infrastructure.Logging.
    ///
    /// Retention policy (same as LogCleanupService):
    /// - Debug, Information, Trace logs: deleted after 48 hours (high volume, low value).
    /// - Warning, Error, Critical logs: kept indefinitely (important for debugging production issues).
    /// </summary>
    [Function("LogCleanup")]
    public async Task CleanupLogs(
        [TimerTrigger("0 0 0 * * *")] TimerInfo timer)
    {
        try
        {
            var cutoff = DateTime.UtcNow - RetentionPeriod;

            // PostgreSQL: NpgsqlConnection replaces SqlConnection for PostgreSQL database access
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            // PostgreSQL: Use double-quoted identifiers instead of SQL Server bracket identifiers [Table]
            cmd.CommandText = @"
                DELETE FROM ""AppLog""
                WHERE ""Level"" IN ('Debug', 'Information', 'Trace')
                  AND ""Timestamp"" < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);

            var deleted = await cmd.ExecuteNonQueryAsync();

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "LogCleanup: Deleted {Count} old Debug/Information log entries", deleted);
            }
        }
        catch (Exception ex)
        {
            // Don't let cleanup failures crash the function.
            // In Azure Functions, this exception is logged to Application Insights automatically.
            _logger.LogError(ex, "LogCleanup: Error during log cleanup");
        }
    }
}
