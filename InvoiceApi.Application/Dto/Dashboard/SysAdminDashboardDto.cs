using InvoiceApi.Application.Dto.AppLog;

namespace InvoiceApi.Application.Dto.Dashboard;

/// <summary>
/// DTO for the SysAdmin-specific dashboard.
/// Shows system-wide statistics (companies, provisioning status) and recent logs.
/// Returned by GET /api/dashboard/sysadmin.
/// </summary>
public class SysAdminDashboardDto
{
    /// <summary>
    /// Total number of companies (issuers) in the system.
    /// </summary>
    public int TotalCompanies { get; set; }

    /// <summary>
    /// Companies that have been provisioned (tenant DB created and migrated).
    /// </summary>
    public int ProvisionedCompanies { get; set; }

    /// <summary>
    /// Companies that are provisioned AND active (accepting requests).
    /// </summary>
    public int ActiveCompanies { get; set; }

    /// <summary>
    /// Companies that do NOT have CompanySystemSettings yet or are not provisioned.
    /// These need SysAdmin attention.
    /// </summary>
    public int PendingCompanies { get; set; }

    /// <summary>
    /// Last 10 Warning/Error/Critical log entries — quick health overview.
    /// </summary>
    public List<AppLogDto> RecentLogs { get; set; } = new();

    /// <summary>
    /// Count of log entries per level (e.g., {"Warning": 15, "Error": 3}).
    /// Used for the summary display.
    /// </summary>
    public Dictionary<string, int> LogCountByLevel { get; set; } = new();
}
