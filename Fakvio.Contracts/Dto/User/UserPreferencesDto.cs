using Fakvio.Contracts.Dto.Dashboard;

namespace Fakvio.Contracts.Dto.User;

/// <summary>
/// Per-user UI preferences returned by GET /api/user-preferences
/// and accepted by PUT /api/user-preferences.
/// The same shape is used for read and update — all fields are always present.
/// </summary>
public class UserPreferencesDto
{
    /// <summary>
    /// Default number of rows shown in data grids.
    /// Allowed values: 10, 25, 50, 100 (grid pager options).
    /// </summary>
    public int DefaultGridPageSize { get; set; } = 10;

    /// <summary>
    /// Customized dashboard widget layout. Null = user never customized it, so the UI
    /// registry defaults apply (see <c>DashboardLayoutMerger</c>). Never contains
    /// duplicate ids — the UI always writes one entry per widget it knows about.
    /// </summary>
    public List<DashboardWidgetLayoutItemDto>? DashboardLayout { get; set; }

    /// <summary>
    /// UTC timestamp when the user dismissed the first-run setup wizard redirect.
    /// Null = never dismissed.
    /// </summary>
    public DateTime? SetupWizardDismissedAt { get; set; }
}
