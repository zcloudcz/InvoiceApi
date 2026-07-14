using Fakvio.Contracts.Dto.User;

namespace Fakvio.Application.Service;

/// <summary>
/// Per-user UI preferences (master DB).
/// Reads return defaults when the user has no preferences row yet;
/// the row is created on first update (lazy upsert).
/// </summary>
public interface IUserPreferencesService
{
    /// <summary>
    /// Returns the preferences of the given user, or defaults when none saved yet.
    /// </summary>
    Task<UserPreferencesDto> GetAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Creates or updates the preferences row of the given user.
    /// Throws <see cref="ArgumentException"/> when a value is outside its allowed set
    /// (e.g. DefaultGridPageSize not one of the pager options).
    /// </summary>
    Task<UserPreferencesDto> UpdateAsync(long userId, UserPreferencesDto dto, CancellationToken ct = default);
}
