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
}
