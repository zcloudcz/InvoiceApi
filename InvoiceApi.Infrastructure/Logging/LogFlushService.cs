using InvoiceApi.Domain.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Logging;

/// <summary>
/// Background service that periodically drains the log queue and batch-inserts entries to the AppLog table.
///
/// Design:
/// - Runs every 5 seconds (configurable via FlushIntervalSeconds)
/// - Drains ALL entries from DatabaseLoggerProvider.LogQueue in one batch
/// - Uses raw ADO.NET (SqlConnection) instead of EF Core to avoid circular logging dependency
///   (EF Core DbContext operations trigger logging → would re-enqueue → infinite loop)
/// - On shutdown, performs a final flush to persist any remaining log entries
///
/// Error handling:
/// - If the database is unavailable, the batch is lost (logs are best-effort, not guaranteed delivery)
/// - The service catches all exceptions to prevent ASP.NET Core from restarting the hosted service
/// </summary>
public class LogFlushService : BackgroundService
{
    private readonly string _connectionString;
    private readonly TimeSpan _flushInterval;
    private readonly ILogger<LogFlushService> _logger;

    public LogFlushService(IConfiguration configuration, ILogger<LogFlushService> logger)
    {
        _connectionString = configuration.GetConnectionString("MasterConnection")
            ?? throw new InvalidOperationException("MasterConnection string not configured for LogFlushService.");
        _flushInterval = TimeSpan.FromSeconds(5);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LogFlushService started — flushing every {Interval}s", _flushInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_flushInterval, stoppingToken);
            await FlushAsync();
        }

        // Final flush on shutdown — persist any remaining log entries
        _logger.LogInformation("LogFlushService shutting down — performing final flush");
        await FlushAsync();
    }

    /// <summary>
    /// Drains the ConcurrentQueue and batch-inserts all entries into the AppLog table.
    /// Uses raw SQL for maximum performance and to avoid EF Core logging circularity.
    /// </summary>
    private async Task FlushAsync()
    {
        // Drain the queue into a local list (non-blocking, lock-free dequeue)
        var batch = new List<AppLog>();
        while (DatabaseLoggerProvider.LogQueue.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        if (batch.Count == 0)
            return;

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Build a batch INSERT statement for all log entries.
            // Using parameterized queries to prevent SQL injection.
            // Each entry gets its own set of parameters (@p0_timestamp, @p0_level, etc.)
            foreach (var entry in batch)
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO [AppLog] ([Timestamp], [Level], [Source], [Message], [Exception], [UserId], [CompanyId], [RequestPath])
                    VALUES (@timestamp, @level, @source, @message, @exception, @userId, @companyId, @requestPath)";

                cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp);
                cmd.Parameters.AddWithValue("@level", entry.Level);
                cmd.Parameters.AddWithValue("@source", entry.Source);
                cmd.Parameters.AddWithValue("@message", entry.Message);
                cmd.Parameters.AddWithValue("@exception", (object?)entry.Exception ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@userId", (object?)entry.UserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@companyId", (object?)entry.CompanyId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@requestPath", (object?)entry.RequestPath ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex)
        {
            // Log to console only — we can't log to the database logger here (infinite loop!)
            Console.Error.WriteLine($"LogFlushService: Failed to flush {batch.Count} log entries: {ex.Message}");
        }
    }
}
