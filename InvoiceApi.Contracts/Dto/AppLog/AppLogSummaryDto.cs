namespace InvoiceApi.Contracts.Dto.AppLog;

/// <summary>
/// Summary of log counts by level — used for the SysAdmin dashboard.
/// Shows how many Warning/Error/Critical logs exist (quick health overview).
/// </summary>
public class AppLogSummaryDto
{
    /// <summary>
    /// Count of log entries per level (e.g., {"Warning": 15, "Error": 3, "Critical": 0}).
    /// </summary>
    public Dictionary<string, int> CountByLevel { get; set; } = new();

    /// <summary>
    /// Total number of log entries across all levels.
    /// </summary>
    public int TotalCount { get; set; }
}
