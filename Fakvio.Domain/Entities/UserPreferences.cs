using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Per-user UI preferences, stored in the MASTER database (1:1 with User).
/// The row is created lazily with defaults on first read — a user without
/// a row simply gets the default values.
///
/// Why a separate entity instead of columns on User?
/// User is an auth/identity entity; preferences will keep growing (page size,
/// language, theme, …) and none of them belong in the auth flows or JWT.
/// Keeping them here means new preferences never touch User or its seed data.
/// </summary>
public class UserPreferences : BaseEntity
{
    /// <summary>
    /// FK to the owning user (master DB). Unique — one preferences row per user.
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// Navigation to the owning user.
    /// </summary>
    public User User { get; set; } = null!;

    /// <summary>
    /// Default number of rows shown in data grids (FakvioGrid RowsPerPage).
    /// Must be one of the pager options (10/25/50/100) — validated in the service.
    /// </summary>
    public int DefaultGridPageSize { get; set; } = 10;

    /// <summary>
    /// Serialized JSON of the dashboard widget layout (list of {id, visible, order}),
    /// see <c>DashboardLayoutMerger</c> in Fakvio.UI.Shared. Null = user never customized
    /// the dashboard, so the registry defaults apply. Stored as raw JSON (not a owned
    /// collection) because the shape is UI-owned and versioned by the UI, not the DB —
    /// an unknown widget id from an older/newer client is simply ignored on merge.
    /// </summary>
    public string? DashboardLayoutJson { get; set; }

    /// <summary>
    /// UTC timestamp when the user dismissed the first-run setup wizard auto-redirect
    /// (/setup). Null = never dismissed, so the redirect decision only depends on
    /// readiness. Set once the user explicitly skips/closes the wizard from the dashboard
    /// redirect — finishing the wizard does not need this, because a finished tenant no
    /// longer has blocking readiness issues and the redirect condition is false anyway.
    /// </summary>
    public DateTime? SetupWizardDismissedAt { get; set; }
}
