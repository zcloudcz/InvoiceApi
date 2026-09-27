using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication.OAuth;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ApiKeyEntity = Fakvio.Domain.Entities.ApiKey;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// The OAuth 2.1 authorization server core (ADR 0001, docs/adr/0001-mcp-oauth21.md §4.2/§4.3).
///
/// Every raw secret (authorization code, refresh token, access token) is generated here, hashed
/// with the SAME algorithm and reasoning as <see cref="ApiKeyService.ComputeHash"/> (32 bytes of
/// CSPRNG entropy — a deterministic hash is safe and lets the lookup stay a single indexed
/// equality query), and the raw value never touches the database or the log.
/// </summary>
public class OAuthService : IOAuthService
{
    /// <summary>Access token lifetime (ADR §4.2 "Životnosti"). Reused by <c>ApiKeyAuthenticator</c> unchanged.</summary>
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>Authorization code lifetime — single-use, so this only needs to survive one redirect round-trip.</summary>
    private static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Refresh token sliding window, capped by the grant's absolute expiry (Q4).</summary>
    private static readonly TimeSpan RefreshTokenSlidingWindow = TimeSpan.FromDays(30);

    /// <summary>Absolute cap on a grant's lifetime, independent of refresh rotation (Q4).</summary>
    private static readonly TimeSpan GrantAbsoluteLifetime = TimeSpan.FromDays(180);

    /// <summary>
    /// Q5 (ADR §9, owner decision): a second use of the SAME refresh token within this window of
    /// its own rotation is a benign concurrent-refresh race (the client retried/duplicated a
    /// request), not theft — it fails with <c>invalid_grant</c> WITHOUT revoking the grant.
    /// Reuse after this window revokes the whole grant (T5).
    /// </summary>
    internal static readonly TimeSpan ConcurrentRefreshGraceWindow = TimeSpan.FromSeconds(10);

    /// <summary>Prefix for OAuth-issued access tokens — shares the ApiKey pipeline (see ApiKey.OAuthGrantId).</summary>
    private const string AccessTokenPrefix = "fak_oat_";

    private readonly MasterDbContext _context;
    private readonly McpOAuthOptions _options;
    private readonly ILogger<OAuthService> _logger;

    public OAuthService(MasterDbContext context, IOptions<McpOAuthOptions> options, ILogger<OAuthService> logger)
    {
        _context = context;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ResolveCanonicalResource(string? requestedResource)
    {
        var canonical = _options.Resource ?? string.Empty;

        if (string.IsNullOrEmpty(requestedResource))
            return canonical;

        if (!string.Equals(requestedResource, canonical, StringComparison.Ordinal))
            throw new OAuthErrorException(OAuthErrorException.InvalidTarget,
                $"resource '{requestedResource}' is not this server's canonical resource");

        return canonical;
    }

    /// <inheritdoc />
    public async Task<OAuthConsentUserInfo> GetConsentUserInfoAsync(long userId, CancellationToken ct = default)
    {
        var user = await _context.User.AsNoTracking()
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            throw new OAuthErrorException(OAuthErrorException.AccessDenied, $"user {userId} not found");

        return new OAuthConsentUserInfo(user.Email, $"{user.FirstName} {user.LastName}".Trim(), user.Company?.CompanyName, IsEligible(user));
    }

    /// <inheritdoc />
    public async Task<string> IssueAuthorizationCodeAsync(IssueAuthorizationCodeRequest request, CancellationToken ct = default)
    {
        var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        if (user is null || !IsEligible(user))
            throw new OAuthErrorException(OAuthErrorException.AccessDenied, "user is not eligible for OAuth (inactive, no company, or not allowlisted)");

        var rawCode = GenerateSecret();

        _context.OAuthAuthorizationCode.Add(new OAuthAuthorizationCode
        {
            CodeHash = ApiKeyService.ComputeHash(rawCode),
            UserId = request.UserId,
            ClientId = request.ClientId,
            ClientName = request.ClientName,
            RedirectUri = request.RedirectUri,
            CodeChallenge = request.CodeChallenge,
            Scopes = request.Scopes,
            Resource = request.Resource,
            ExpiresAt = DateTime.UtcNow.Add(AuthorizationCodeLifetime)
        });

        await _context.SaveChangesAsync(ct);
        return rawCode;
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> ExchangeAuthorizationCodeAsync(ExchangeAuthorizationCodeRequest request, CancellationToken ct = default)
    {
        var hash = ApiKeyService.ComputeHash(request.Code);
        var code = await _context.OAuthAuthorizationCode.FirstOrDefaultAsync(c => c.CodeHash == hash, ct);

        if (code is null)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "unknown authorization code");

        if (!string.Equals(code.ClientId, request.ClientId, StringComparison.Ordinal))
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "client_id does not match the authorization request");

        if (!string.Equals(code.RedirectUri, request.RedirectUri, StringComparison.Ordinal))
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "redirect_uri does not match the authorization request");

        if (request.Resource is not null && !string.Equals(request.Resource, code.Resource, StringComparison.Ordinal))
            throw new OAuthErrorException(OAuthErrorException.InvalidTarget, "resource does not match the authorization request");

        if (!VerifyPkce(request.CodeVerifier, code.CodeChallenge))
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "PKCE code_verifier does not match the authorization request's code_challenge");

        // PostgresDateTime.ToUtc: see its docs — Npgsql.EnableLegacyTimestampBehavior means
        // this column comes back Kind=Local, not Utc, and comparing it raw against UtcNow
        // would let an expired code keep working (same class of bug as issue #236).
        if (PostgresDateTime.ToUtc(code.ExpiresAt) <= DateTime.UtcNow)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "authorization code has expired");

        // Atomic single-use consumption (T3): only one caller can win this UPDATE.
        var consumedCount = await _context.OAuthAuthorizationCode
            .Where(c => c.Id == code.Id && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, DateTime.UtcNow), ct);

        if (consumedCount == 0)
        {
            // Reuse — the code was already redeemed (by this request or a concurrent one).
            _logger.LogWarning("OAuth.CodeReuseDetected: authorization code for user {UserId} client {ClientId} reused",
                code.UserId, code.ClientId);

            if (code.GrantId is { } existingGrantId)
                await RevokeGrantAsync(existingGrantId, EOAuthGrantRevokedReason.CodeReuse, null, ct);

            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "authorization code has already been used");
        }

        var user = await _context.User.FirstOrDefaultAsync(u => u.Id == code.UserId, ct);
        if (user is null || !IsEligible(user))
            throw new OAuthErrorException(OAuthErrorException.AccessDenied, "user is not eligible for OAuth (inactive, no company, or not allowlisted)");

        // Supersede any existing grant for the same (user, client, resource) — ADR §4.3: a
        // reconnect must not pile up rows in "Připojené aplikace".
        var priorGrantIds = await _context.OAuthGrant
            .Where(g => g.UserId == code.UserId && g.ClientId == code.ClientId && g.Resource == code.Resource && g.RevokedAt == null)
            .Select(g => g.Id)
            .ToListAsync(ct);

        foreach (var priorGrantId in priorGrantIds)
            await RevokeGrantAsync(priorGrantId, EOAuthGrantRevokedReason.Superseded, null, ct);

        var grant = new OAuthGrant
        {
            UserId = code.UserId,
            ClientId = code.ClientId,
            ClientName = code.ClientName,
            Scopes = code.Scopes,
            Resource = code.Resource,
            LastUsedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(GrantAbsoluteLifetime)
        };
        _context.OAuthGrant.Add(grant);
        await _context.SaveChangesAsync(ct); // flush to obtain grant.Id

        await _context.OAuthAuthorizationCode
            .Where(c => c.Id == code.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.GrantId, grant.Id), ct);

        var (rawAccess, accessEntity) = CreateAccessToken(grant, grant.Scopes);
        var (rawRefresh, refreshEntity) = CreateRefreshToken(grant);

        _context.ApiKey.Add(accessEntity);
        _context.OAuthRefreshToken.Add(refreshEntity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("OAuth.TokenIssued: grant {GrantId} client {ClientId} user {UserId} scope {Scope}",
            grant.Id, grant.ClientId, grant.UserId, grant.Scopes);

        return new OAuthTokenResult(rawAccess, rawRefresh, (int)AccessTokenLifetime.TotalSeconds, grant.Scopes);
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        var hash = ApiKeyService.ComputeHash(request.RefreshToken);
        var token = await _context.OAuthRefreshToken
            .Include(t => t.Grant)
            .ThenInclude(g => g.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "unknown refresh token");

        var grant = token.Grant;

        // PostgresDateTime.ToUtc on every stored timestamp compared against UtcNow below —
        // see that helper's docs for why (issue #236-class bug: Npgsql.EnableLegacyTimestampBehavior).
        if (grant.RevokedAt is not null || PostgresDateTime.ToUtc(grant.ExpiresAt) <= DateTime.UtcNow)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "grant is revoked or has reached its absolute expiry");

        if (PostgresDateTime.ToUtc(token.ExpiresAt) <= DateTime.UtcNow)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "refresh token has expired");

        if (request.Resource is not null && !string.Equals(request.Resource, grant.Resource, StringComparison.Ordinal))
            throw new OAuthErrorException(OAuthErrorException.InvalidTarget, "resource does not match the grant");

        // Computed (and validated — T12) BEFORE consuming the token, so a rejected scope
        // request never rotates the refresh token it was rejected on.
        var scope = NormalizeRequestedScope(request.Scope, grant.Scopes);

        if (token.ConsumedAt is not null)
            await HandleRefreshReuseAsync(token.Id, grant.Id, PostgresDateTime.ToUtc(token.ConsumedAt.Value), ct);

        var consumedCount = await _context.OAuthRefreshToken
            .Where(t => t.Id == token.Id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, DateTime.UtcNow), ct);

        if (consumedCount == 0)
        {
            // Lost a race against a concurrent refresh that consumed it a moment ago —
            // re-read the timestamp the winner just wrote and apply the same Q5 window.
            var actualConsumedAt = await _context.OAuthRefreshToken
                .AsNoTracking()
                .Where(t => t.Id == token.Id)
                .Select(t => t.ConsumedAt)
                .FirstAsync(ct);

            await HandleRefreshReuseAsync(token.Id, grant.Id, PostgresDateTime.ToUtc(actualConsumedAt!.Value), ct);
        }

        var user = grant.User;
        if (user is null || !IsEligible(user))
        {
            if (user is not null && !user.IsActive)
                await RevokeGrantAsync(grant.Id, EOAuthGrantRevokedReason.Admin, null, ct);

            throw new OAuthErrorException(OAuthErrorException.AccessDenied, "user is no longer eligible for OAuth");
        }

        var (rawAccess, accessEntity) = CreateAccessToken(grant, scope);
        var (rawRefresh, refreshEntity) = CreateRefreshToken(grant);

        _context.ApiKey.Add(accessEntity);
        _context.OAuthRefreshToken.Add(refreshEntity);
        grant.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("OAuth.Refreshed: grant {GrantId} scope {Scope}", grant.Id, scope);

        return new OAuthTokenResult(rawAccess, rawRefresh, (int)AccessTokenLifetime.TotalSeconds, scope);
    }

    /// <summary>
    /// Always throws — either a quiet <c>invalid_grant</c> (Q5 grace window) or, outside the
    /// window, a full grant revocation (T5) followed by <c>invalid_grant</c>.
    /// </summary>
    private async Task HandleRefreshReuseAsync(long tokenId, long grantId, DateTime consumedAt, CancellationToken ct)
    {
        var elapsed = DateTime.UtcNow - consumedAt;

        if (elapsed <= ConcurrentRefreshGraceWindow)
        {
            // Benign — e.g. Claude fired two refreshes for the same token concurrently. The
            // other request already returned the new tokens; this one simply fails.
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant,
                "refresh token already used (concurrent refresh within the grace window)");
        }

        _logger.LogWarning("OAuth.RefreshReuseDetected: grant {GrantId} refresh token {TokenId} reused after the grace window",
            grantId, tokenId);

        await RevokeGrantAsync(grantId, EOAuthGrantRevokedReason.RefreshReuse, null, ct);

        throw new OAuthErrorException(OAuthErrorException.InvalidGrant,
            "refresh token reuse detected outside the grace window — grant revoked");
    }

    /// <inheritdoc />
    public async Task RevokeAsync(string token, string? tokenTypeHint, CancellationToken ct = default)
    {
        // RFC 7009 §2.2: an unknown/already-invalid token is not an error either way, so both
        // branches simply return without throwing when nothing matches.
        if (await TryRevokeAsRefreshTokenAsync(token, ct))
            return;

        await TryRevokeAsAccessTokenAsync(token, ct);
    }

    private async Task<bool> TryRevokeAsRefreshTokenAsync(string rawToken, CancellationToken ct)
    {
        var hash = ApiKeyService.ComputeHash(rawToken);
        var grantId = await _context.OAuthRefreshToken
            .Where(t => t.TokenHash == hash)
            .Select(t => (long?)t.GrantId)
            .FirstOrDefaultAsync(ct);

        if (grantId is null)
            return false;

        await RevokeGrantAsync(grantId.Value, EOAuthGrantRevokedReason.User, null, ct);
        return true;
    }

    private async Task<bool> TryRevokeAsAccessTokenAsync(string rawToken, CancellationToken ct)
    {
        var hash = ApiKeyService.ComputeHash(rawToken);
        var now = DateTime.UtcNow;

        var updated = await _context.ApiKey
            .Where(k => k.KeyHash == hash && k.OAuthGrantId != null && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), ct);

        return updated > 0;
    }

    /// <inheritdoc />
    public async Task RevokeGrantAsync(long grantId, EOAuthGrantRevokedReason reason, long? revokedByUserId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        var grantUpdated = await _context.OAuthGrant
            .Where(g => g.Id == grantId && g.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.RevokedAt, now)
                .SetProperty(g => g.RevokedByUserId, revokedByUserId)
                .SetProperty(g => g.RevokedReason, reason), ct);

        // Always sweep — even when the grant row was already revoked — so a token created
        // between an earlier partial failure and now can never be left alive under a
        // grant that already reads as revoked everywhere else (ADR §4.3/§4.8: "najednou").
        await _context.ApiKey
            .Where(k => k.OAuthGrantId == grantId && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(k => k.RevokedAt, now)
                .SetProperty(k => k.RevokedByUserId, revokedByUserId), ct);

        if (grantUpdated > 0)
            _logger.LogWarning("OAuth.GrantRevoked: grant {GrantId} reason {Reason}", grantId, reason);
    }

    /// <inheritdoc />
    public async Task RevokeAllGrantsForUserAsync(long userId, EOAuthGrantRevokedReason reason, CancellationToken ct = default)
    {
        var grantIds = await _context.OAuthGrant
            .Where(g => g.UserId == userId && g.RevokedAt == null)
            .Select(g => g.Id)
            .ToListAsync(ct);

        foreach (var grantId in grantIds)
            await RevokeGrantAsync(grantId, reason, null, ct);
    }

    // ─── Eligibility (allowlist + Q9) ───────────────────────────────────────

    /// <summary>
    /// ADR §4.7/Q9/§5.1: a user must be active, belong to a company (SysAdmin never gets OAuth),
    /// and be on the allowlist (or AllowAll must be on). Checked at consent, at the token
    /// endpoint (both grant types), and — via <c>ApiKeyAuthenticator</c>'s revocation checks —
    /// implicitly at every subsequent request.
    /// </summary>
    private bool IsEligible(Domain.Entities.User user)
    {
        if (!user.IsActive) return false;
        if (!user.CompanyId.HasValue) return false;
        if (_options.AllowAll) return true;
        if (_options.AllowedUserIds.Contains(user.Id)) return true;
        return user.CompanyId.HasValue && _options.AllowedCompanyIds.Contains(user.CompanyId.Value);
    }

    // ─── Token/code generation + validation ─────────────────────────────────

    private static (string Raw, ApiKeyEntity Entity) CreateAccessToken(OAuthGrant grant, string scope)
    {
        var raw = AccessTokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        var entity = new ApiKeyEntity
        {
            UserId = grant.UserId,
            Name = $"OAuth: {grant.ClientName}",
            KeyPrefix = raw[..12],
            KeyHash = ApiKeyService.ComputeHash(raw),
            Scopes = scope,
            OAuthGrantId = grant.Id,
            ExpiresAt = DateTime.UtcNow.Add(AccessTokenLifetime)
        };

        return (raw, entity);
    }

    private static (string Raw, OAuthRefreshToken Entity) CreateRefreshToken(OAuthGrant grant)
    {
        var raw = GenerateSecret();
        var slidingExpiry = DateTime.UtcNow.Add(RefreshTokenSlidingWindow);
        // Capped by the grant's absolute expiry (Q4) — a refresh minted near the 180-day
        // ceiling must not outlive the grant that issued it. grant.ExpiresAt may have just
        // been read back from PostgreSQL (Kind=Local under the legacy timestamp switch) when
        // called from RefreshAsync, so it goes through PostgresDateTime.ToUtc like every other
        // stored timestamp before it is compared or stored onward.
        var grantExpiresAtUtc = PostgresDateTime.ToUtc(grant.ExpiresAt);
        var expiresAt = slidingExpiry < grantExpiresAtUtc ? slidingExpiry : grantExpiresAtUtc;

        var entity = new OAuthRefreshToken
        {
            TokenHash = ApiKeyService.ComputeHash(raw),
            GrantId = grant.Id,
            ExpiresAt = expiresAt
        };

        return (raw, entity);
    }

    /// <summary>32 CSPRNG bytes, Base64Url-encoded — used for authorization codes and refresh tokens alike.</summary>
    private static string GenerateSecret() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// PKCE S256 verification (ADR §4.2, §4.6: plain is never accepted — there is no branch for
    /// it here at all). Constant-time compare per the ADR's explicit instruction, even though
    /// both sides are already non-secret hash outputs by this point.
    /// </summary>
    private static bool VerifyPkce(string codeVerifier, string codeChallenge)
    {
        if (string.IsNullOrEmpty(codeVerifier))
            return false;

        var computedChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return FixedTimeEquals(computedChallenge, codeChallenge);
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var bytesA = Encoding.UTF8.GetBytes(a);
        var bytesB = Encoding.UTF8.GetBytes(b);
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }

    /// <summary>
    /// Validates an optional <c>scope</c> parameter on a refresh request against the grant's
    /// scope (T12 — refresh must never WIDEN scope, only narrow or leave it unchanged).
    /// Space-delimited per RFC 6749, normalized to the same canonical comma-joined form
    /// <see cref="ApiKeyService"/> uses.
    /// </summary>
    private static string NormalizeRequestedScope(string? requested, string grantedScopes)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return grantedScopes;

        var grantedSet = grantedScopes.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var requestedParts = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (requestedParts.Length == 0)
            return grantedScopes;

        foreach (var part in requestedParts)
        {
            if (!grantedSet.Contains(part, StringComparer.OrdinalIgnoreCase))
                throw new OAuthErrorException(OAuthErrorException.InvalidScope,
                    $"requested scope '{part}' exceeds the scope granted to this client");
        }

        var canWrite = requestedParts.Contains("write", StringComparer.OrdinalIgnoreCase);
        return canWrite ? "read,write" : "read";
    }
}
