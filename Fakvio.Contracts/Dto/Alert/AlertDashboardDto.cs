namespace Fakvio.Contracts.Dto.Alert;

/// <summary>
/// Summary of open (unresolved) alerts for the Dashboard "Upozornění" tile.
/// Includes the total count plus the first 5 items for the inline list.
/// </summary>
public class AlertDashboardDto
{
    /// <summary>Total number of open alerts for the current tenant.</summary>
    public int TotalOpenCount { get; set; }

    /// <summary>
    /// Up to 5 most recent open alerts, sorted by CreatedAt descending.
    /// Displayed in the dashboard tile list.
    /// </summary>
    public List<AlertDto> RecentAlerts { get; set; } = [];
}
