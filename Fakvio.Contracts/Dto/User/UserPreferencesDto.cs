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
}
