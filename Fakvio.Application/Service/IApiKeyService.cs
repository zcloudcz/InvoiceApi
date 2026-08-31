using Fakvio.Contracts.Dto.ApiKey;

namespace Fakvio.Application.Service;

/// <summary>
/// Management of a user's own API keys (master DB).
/// Every operation is scoped to the calling user — there is no cross-user access,
/// so an id from another user simply behaves as "not found".
/// </summary>
public interface IApiKeyService
{
    /// <summary>
    /// Lists the user's keys, newest first. Never returns the raw key or its hash.
    /// </summary>
    Task<IReadOnlyList<ApiKeyDto>> GetAllAsync(long userId, CancellationToken ct = default);

    /// <summary>
    /// Creates a key and returns it together with the raw secret — the only time
    /// the secret is available.
    /// Throws <see cref="ArgumentException"/> for an empty name, unknown scope
    /// or an expiration that is not in the future.
    /// </summary>
    Task<CreatedApiKeyDto> CreateAsync(long userId, CreateApiKeyDto dto, CancellationToken ct = default);

    /// <summary>
    /// Soft-revokes the user's key. Returns false when the key does not exist,
    /// belongs to someone else, or was already revoked.
    /// </summary>
    Task<bool> RevokeAsync(long userId, long id, CancellationToken ct = default);
}
