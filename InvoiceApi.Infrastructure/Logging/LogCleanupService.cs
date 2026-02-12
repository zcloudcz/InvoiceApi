using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Logging;

/// <summary>
/// Background service that periodically cleans up old log entries from the AppLog table.
///
/// Retention policy:
/// - Debug and Information logs: deleted after 48 hours (high volume, low value)
/// - Warning, Error, Critical logs: kept indefinitely (important for debugging production issues)
///
/// Runs once per hour. Uses raw ADO.NET to avoid EF Core overhead for a simple DELETE.
/// </summary>
public class LogCleanupService : BackgroundService
{
    private readonly string _connectionString;
    private readonly ILogger<LogCleanupService> _logger;

    /// <summary>
    /// How long to keep Debug/Information logs before auto-deletion.
    /// </summary>
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromHours(48);

    /// <summary>
    /// How often the cleanup job runs.
    /// </summary>
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);

    public LogCleanupService(IConfiguration configuration, ILogger<LogCleanupService> logger)
    {
        _connectionString = configuration.GetConnectionString("MasterConnection")
            ?? throw new InvalidOperationException("MasterConnection string not configured for LogCleanupService.");
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "LogCleanupService started — cleaning Debug/Information logs older than {Hours}h every {Interval}h",
            RetentionPeriod.TotalHours, CleanupInterval.TotalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CleanupInterval, stoppingToken);

            try
            {
                var cutoff = DateTime.UtcNow - RetentionPeriod;

                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(stoppingToken);

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM [AppLog]
                    WHERE [Level] IN ('Debug', 'Information', 'Trace')
                      AND [Timestamp] < @cutoff";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);

                var deleted = await cmd.ExecuteNonQueryAsync(stoppingToken);

                if (deleted > 0)
                {
                    _logger.LogInformation("LogCleanupService: Deleted {Count} old Debug/Information log entries", deleted);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown — exit gracefully
                break;
            }
            catch (Exception ex)
            {
                // Don't let cleanup failures crash the service
                _logger.LogError(ex, "LogCleanupService: Error during log cleanup");
            }
        }
    }
}
