using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ApiKeyEntity = Fakvio.Domain.Entities.ApiKey;

namespace Fakvio.Infrastructure.Authentication;

/// <summary>
/// Validates a raw API key against the master database and builds the request principal.
/// The only implementation of <see cref="IApiKeyAuthenticator"/>; both hosts call it
/// through their own thin driver (see CLAUDE.md — "API + Functions duplication").
/// </summary>
public class ApiKeyAuthenticator : IApiKeyAuthenticator
{
    /// <summary>
    /// How stale <c>LastUsedAt</c> is allowed to get before we write it again.
    /// One AI turn fires dozens of tool calls; updating on every request would turn a
    /// read-only burst into dozens of master DB writes for information nobody reads
    /// at that resolution.
    /// </summary>
    private static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(5);

    private readonly MasterDbContext _context;
    private readonly ILogger<ApiKeyAuthenticator> _logger;

    public ApiKeyAuthenticator(MasterDbContext context, ILogger<ApiKeyAuthenticator> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ClaimsPrincipal?> AuthenticateAsync(string rawKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey)
            || !rawKey.StartsWith(ApiKeyAuthenticationDefaults.RawKeyPrefix, StringComparison.Ordinal))
            return null;

        // Lookup is a single indexed equality on the unique IX_ApiKey_KeyHash — no scan,
        // no per-row verify, and therefore no need for a constant-time comparison:
        // the database never compares the secret itself, only its hash.
        var hash = ApiKeyService.ComputeHash(rawKey);

        var key = await _context.ApiKey
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.KeyHash == hash, ct);

        if (key is null)
        {
            // The presented prefix is the same 12 characters the owner sees in the UI,
            // so it correlates a failing client with a key without being usable itself.
            // The rest of the key never reaches the log.
            _logger.LogWarning("API key authentication failed: unknown key {KeyPrefix}", SafePrefix(rawKey));
            return null;
        }

        if (!IsUsable(key))
            return null;

        await TouchLastUsedAsync(key, ct);

        return BuildPrincipal(key);
    }

    /// <summary>
    /// Fail-closed gate: every reason a key must stop working, in one place.
    /// The caller only learns "no" — the specific reason stays in the log.
    /// </summary>
    private bool IsUsable(ApiKeyEntity key)
    {
        if (key.RevokedAt is not null)
        {
            _logger.LogWarning("API key authentication failed: key {KeyPrefix} is revoked", key.KeyPrefix);
            return false;
        }

        if (key.ExpiresAt is not null && ToUtc(key.ExpiresAt.Value) <= DateTime.UtcNow)
        {
            _logger.LogWarning("API key authentication failed: key {KeyPrefix} expired at {ExpiresAt}",
                key.KeyPrefix, key.ExpiresAt);
            return false;
        }

        // A key outlives the login session, so deactivating a user has to kill their keys
        // too — otherwise a fired employee's MCP client keeps working indefinitely.
        if (!key.User.IsActive)
        {
            _logger.LogWarning("API key authentication failed: owner of key {KeyPrefix} is deactivated", key.KeyPrefix);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reinterprets a timestamp read back from the database as UTC.
    ///
    /// This is not decoration. Both hosts switch on <c>Npgsql.EnableLegacyTimestampBehavior</c>,
    /// under which a <c>timestamp with time zone</c> column is read back as a DateTime
    /// *converted to the server's local time* with <see cref="DateTimeKind.Local"/>.
    /// Comparing that value straight against <see cref="DateTime.UtcNow"/> is off by the
    /// local UTC offset — and in any zone east of Greenwich it is off in the dangerous
    /// direction: an expired key would keep authenticating for another offset's worth of
    /// hours (two, in CEST).
    ///
    /// Unspecified is treated as UTC rather than converted, matching
    /// <c>MasterDbContext.NormalizeDateTimesToUtc</c>, which stamps every stored DateTime
    /// as UTC without shifting it. Converting here instead would undo that on providers
    /// that round-trip the kind verbatim (EF InMemory).
    /// </summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>
    /// Claims mirror <c>AuthService.GenerateJwtTokenAsync</c> one-for-one, plus the two
    /// API-key-specific ones. Downstream (ImpersonationMiddleware, TenantContextMiddleware,
    /// every <c>[Authorize(Roles = …)]</c>) therefore cannot tell the two credentials apart.
    ///
    /// The authentication type is load-bearing: without it <c>IsAuthenticated</c> is false
    /// and every endpoint answers 401 no matter how valid the key is.
    ///
    /// The name and role claim types happen to equal ClaimsIdentity's own defaults, so they
    /// are spelled out for intent rather than for effect: a wrong role claim type makes
    /// every <c>[Authorize(Roles = …)]</c> answer 403 without a word in the log, and that is
    /// too quiet a failure to leave implicit.
    /// </summary>
    private static ClaimsPrincipal BuildPrincipal(ApiKeyEntity key)
    {
        var user = key.User;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(ApiKeyAuthenticationDefaults.ScopeClaimType, key.Scopes),
            new(ApiKeyAuthenticationDefaults.KeyIdClaimType, key.Id.ToString())
        };

        // Same conditional as the JWT path: a SysAdmin has no company, which is what
        // lets ImpersonationMiddleware fill the claim in from X-Company-Id.
        if (user.CompanyId.HasValue)
            claims.Add(new Claim("CompanyId", user.CompanyId.Value.ToString()));

        var identity = new ClaimsIdentity(
            claims,
            ApiKeyAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);

        return new ClaimsPrincipal(identity);
    }

    /// <summary>
    /// Records coarse "last seen" on the key. Best effort on purpose: a lost write here
    /// is a slightly stale timestamp in a settings grid, while throwing would turn it
    /// into a failed request for a perfectly valid key.
    /// </summary>
    private async Task TouchLastUsedAsync(ApiKeyEntity key, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        if (key.LastUsedAt is not null && ToUtc(key.LastUsedAt.Value) > now - LastUsedWriteInterval)
            return;

        key.LastUsedAt = now;

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update LastUsedAt of API key {KeyPrefix}", key.KeyPrefix);
        }
    }

    /// <summary>
    /// The leading, non-secret part of a presented key, for log correlation only.
    /// Guards against a short/garbage token so logging can never throw.
    /// </summary>
    private static string SafePrefix(string rawKey)
        => rawKey.Length <= 12 ? rawKey : rawKey[..12];
}
