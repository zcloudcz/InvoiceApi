using System.Security.Cryptography;
using System.Text;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.OAuth;
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
    public async Task<OAuthConsentUserInfo> GetConsentUserInfoAsync(long userId, CancellationToken ct = default, long? companyId = null)
    {
        var user = await _context.User.AsNoTracking()
            .Include(u => u.Company)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            throw new OAuthErrorException(OAuthErrorException.AccessDenied, $"user {userId} not found");

        var selectedCompanyId = companyId ?? user.CompanyId;
        var companyName = await _context.Client.AsNoTracking()
            .Where(c => c.Id == selectedCompanyId).Select(c => c.CompanyName).FirstOrDefaultAsync(ct);
        return new OAuthConsentUserInfo(user.Email, $"{user.FirstName} {user.LastName}".Trim(), companyName,
            await IsEligibleAsync(user, selectedCompanyId, ct));
    }

    /// <inheritdoc />
    public async Task<string> IssueAuthorizationCodeAsync(IssueAuthorizationCodeRequest request, CancellationToken ct = default)
    {
        var user = await _context.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UserId, ct);
        var companyId = request.CompanyId ?? user?.CompanyId;
        if (user is null || !await IsEligibleAsync(user, companyId, ct))
            throw new OAuthErrorException(OAuthErrorException.AccessDenied, "user is not eligible for OAuth (inactive, no company, or not allowlisted)");

        var rawCode = GenerateSecret();

        _context.OAuthAuthorizationCode.Add(new OAuthAuthorizationCode
        {
            CodeHash = ApiKeyService.ComputeHash(rawCode),
            UserId = request.UserId,
            CompanyId = companyId,
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
        // Production enables Npgsql retries. EF requires the complete transaction to run
        // inside its execution strategy, otherwise even the first query fails with HTTP 500.
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(
            cancel => ExchangeAuthorizationCodeInTransactionAsync(request, cancel), ct);
    }

    private async Task<OAuthTokenResult> ExchangeAuthorizationCodeInTransactionAsync(
        ExchangeAuthorizationCodeRequest request, CancellationToken ct)
    {
        // A retry must reload database state instead of reusing entities mutated by a failed
        // attempt. The authorization code is always looked up again under its row lock below.
        _context.ChangeTracker.Clear();
        var hash = ApiKeyService.ComputeHash(request.Code);

        // Codex review finding (high, T3): the previous version consumed the code with an
        // atomic ExecuteUpdateAsync, but linked it to the grant it produced (code.GrantId) in a
        // SEPARATE later statement. That left a window where a concurrent redeemer could see
        // "already consumed" (ConsumedAt set) WITHOUT yet seeing GrantId — so the reuse it just
        // detected had nothing to revoke. A `SELECT … FOR UPDATE` inside an explicit transaction
        // closes that window completely: a second request for the SAME code blocks on this
        // query until the first one's transaction commits (or rolls back), and by the time it
        // is unblocked it sees either "not consumed" (first request failed/rolled back) or
        // "consumed AND linked to a grant" (first request succeeded) — never the in-between
        // state. This also means the ConsumedAt write below is a plain property assignment, not
        // a conditional ExecuteUpdateAsync — the row lock is what makes it safe.
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        var code = (await _context.OAuthAuthorizationCode
                .FromSqlInterpolated($"SELECT * FROM \"OAuthAuthorizationCode\" WHERE \"CodeHash\" = {hash} FOR UPDATE")
                .ToListAsync(ct))
            .SingleOrDefault();

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

        if (code.ConsumedAt is not null)
        {
            // Reuse (T3) — thanks to the row lock above, GrantId here is guaranteed to be the
            // FINAL value the winning redemption wrote (never an in-between null), so this
            // always has something to revoke when a grant was actually produced.
            _logger.LogWarning("OAuth.CodeReuseDetected: authorization code for user {UserId} client {ClientId} reused",
                code.UserId, code.ClientId);

            if (code.GrantId is { } existingGrantId)
                await RevokeGrantAsync(existingGrantId, EOAuthGrantRevokedReason.CodeReuse, null, ct);

            await transaction.CommitAsync(ct); // persist the revoke-on-reuse side effect above
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "authorization code has already been used");
        }

        code.ConsumedAt = DateTime.UtcNow; // plain assignment — safe under the exclusive row lock

        var user = await _context.User.FirstOrDefaultAsync(u => u.Id == code.UserId, ct);
        if (user is null || !await IsEligibleAsync(user, code.CompanyId, ct))
        {
            await _context.SaveChangesAsync(ct); // persist the consumption so a denied code cannot be retried
            await transaction.CommitAsync(ct);
            throw new OAuthErrorException(OAuthErrorException.AccessDenied, "user is not eligible for OAuth (inactive, no company, or not allowlisted)");
        }

        // Supersede only the same (user, company, client, resource). Consent to another
        // company must not revoke or replace this company's independent authorization.
        // ADR §4.3: a
        // reconnect must not pile up rows in "Připojené aplikace".
        var priorGrantIds = await _context.OAuthGrant
            .Where(g => g.UserId == code.UserId && g.CompanyId == code.CompanyId && g.ClientId == code.ClientId && g.Resource == code.Resource && g.RevokedAt == null)
            .Select(g => g.Id)
            .ToListAsync(ct);

        foreach (var priorGrantId in priorGrantIds)
            await RevokeGrantAsync(priorGrantId, EOAuthGrantRevokedReason.Superseded, null, ct);

        var grant = new OAuthGrant
        {
            UserId = code.UserId,
            CompanyId = code.CompanyId,
            ClientId = code.ClientId,
            ClientName = code.ClientName,
            Scopes = code.Scopes,
            Resource = code.Resource,
            LastUsedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.Add(GrantAbsoluteLifetime)
        };
        _context.OAuthGrant.Add(grant);
        await _context.SaveChangesAsync(ct); // flush (still inside the transaction) to obtain grant.Id

        code.GrantId = grant.Id;

        var (rawAccess, accessEntity) = CreateAccessToken(grant, grant.Scopes);
        var (rawRefresh, refreshEntity) = CreateRefreshToken(grant);

        _context.ApiKey.Add(accessEntity);
        _context.OAuthRefreshToken.Add(refreshEntity);
        await _context.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        _logger.LogInformation("OAuth.TokenIssued: grant {GrantId} client {ClientId} user {UserId} scope {Scope}",
            grant.Id, grant.ClientId, grant.UserId, grant.Scopes);

        return new OAuthTokenResult(rawAccess, rawRefresh, ExpiresInSeconds(accessEntity), grant.Scopes);
    }

    /// <inheritdoc />
    public async Task<OAuthTokenResult> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(cancel => RefreshInTransactionAsync(request, cancel), ct);
    }

    private async Task<OAuthTokenResult> RefreshInTransactionAsync(RefreshTokenRequest request, CancellationToken ct)
    {
        // Discard tracked values from a rolled-back attempt before checking token reuse.
        _context.ChangeTracker.Clear();
        var hash = ApiKeyService.ComputeHash(request.RefreshToken);

        // Codex review finding (high, T5): the previous version read the grant's RevokedAt
        // OUTSIDE any lock, then — much later, after creating and saving new tokens — had no
        // way to notice a revocation that landed in between. A concurrent RevokeGrantAsync
        // could finish its two ExecuteUpdateAsync statements (grant + existing access tokens)
        // entirely inside that window and never see the brand-new access token this refresh
        // was about to insert, leaving a live token under a "revoked" grant for up to 1h.
        //
        // Locking the GRANT row with `FOR UPDATE` inside an explicit transaction serializes
        // refresh against revoke completely: RevokeGrantAsync's own ExecuteUpdateAsync on the
        // same row (same table, same PK) is a normal UPDATE, and Postgres blocks a normal
        // UPDATE against a row until whoever holds `FOR UPDATE` on it commits or rolls back.
        // So the two operations can no longer interleave — either refresh finishes first (and
        // revoke's later sweep sees and revokes the new token too), or revoke finishes first
        // (and refresh's own re-read of the row, taken under the lock, sees RevokedAt already
        // set and refuses).
        await using var transaction = await _context.Database.BeginTransactionAsync(ct);

        // Codex review, round 2 (high — the round-1 fix was incomplete): only the GrantId is
        // resolved before the lock, via an untracked projection. The token row itself is loaded
        // AFTER the grant lock is held, not before — loading it first (as round 1 did) let two
        // concurrent refreshes of the SAME token each capture their own "ConsumedAt == null"
        // snapshot before either one waited on anything, so both could still pass the
        // token.ConsumedAt check further down even though the grant lock now serialized
        // everything else. Nothing else in this class ever reads-then-writes
        // OAuthRefreshToken.ConsumedAt without first holding this same grant lock, so loading
        // the token after acquiring it is what actually makes the read authoritative.
        var tokenGrantId = await _context.OAuthRefreshToken.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => (long?)t.GrantId)
            .FirstOrDefaultAsync(ct);

        if (tokenGrantId is null)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "unknown refresh token");

        var grant = (await _context.OAuthGrant
                .FromSqlInterpolated($"SELECT * FROM \"OAuthGrant\" WHERE \"Id\" = {tokenGrantId.Value} FOR UPDATE")
                .ToListAsync(ct))
            .SingleOrDefault();

        if (grant is null)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "grant no longer exists");

        // Loaded only NOW, with the grant's exclusive lock already held — see the comment above
        // for why this ordering (not a second row lock on the token itself) is what closes the
        // same-token concurrent-refresh race.
        var token = await _context.OAuthRefreshToken.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
            throw new OAuthErrorException(OAuthErrorException.InvalidGrant, "unknown refresh token");

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
        {
            // Reuse — with the grant row locked (and this SAME token row already loaded in
            // this transaction), "already consumed" here is the true, final state: no
            // concurrent refresh of the SAME token could still be in flight once we hold the
            // grant's lock, because every refresh of this grant takes that same lock first.
            var elapsed = DateTime.UtcNow - PostgresDateTime.ToUtc(token.ConsumedAt.Value);

            if (elapsed <= ConcurrentRefreshGraceWindow)
            {
                // Q5 — benign concurrent-refresh race, no revocation.
                throw new OAuthErrorException(OAuthErrorException.InvalidGrant,
                    "refresh token already used (concurrent refresh within the grace window)");
            }

            _logger.LogWarning("OAuth.RefreshReuseDetected: grant {GrantId} refresh token {TokenId} reused after the grace window",
                grant.Id, token.Id);

            await RevokeGrantAsync(grant.Id, EOAuthGrantRevokedReason.RefreshReuse, null, ct);
            await transaction.CommitAsync(ct); // persist the revoke-on-reuse side effect above

            throw new OAuthErrorException(OAuthErrorException.InvalidGrant,
                "refresh token reuse detected outside the grace window — grant revoked");
        }

        token.ConsumedAt = DateTime.UtcNow; // plain assignment — safe under the grant's exclusive row lock

        var user = await _context.User.FirstOrDefaultAsync(u => u.Id == grant.UserId, ct);
        if (user is null || !await IsEligibleAsync(user, grant.CompanyId, ct))
        {
            if (user is not null && !user.IsActive)
                await RevokeGrantAsync(grant.Id, EOAuthGrantRevokedReason.Admin, null, ct);

            await _context.SaveChangesAsync(ct); // persist the consumption regardless
            await transaction.CommitAsync(ct);
            throw new OAuthErrorException(OAuthErrorException.AccessDenied, "user is no longer eligible for OAuth");
        }

        var (rawAccess, accessEntity) = CreateAccessToken(grant, scope);
        var (rawRefresh, refreshEntity) = CreateRefreshToken(grant);

        _context.ApiKey.Add(accessEntity);
        _context.OAuthRefreshToken.Add(refreshEntity);
        grant.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);

        _logger.LogInformation("OAuth.Refreshed: grant {GrantId} scope {Scope}", grant.Id, scope);

        return new OAuthTokenResult(rawAccess, rawRefresh, ExpiresInSeconds(accessEntity), scope);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<OAuthGrantDto>> GetGrantsAsync(long userId, CancellationToken ct = default)
    {
        return await _context.OAuthGrant
            .AsNoTracking()
            .Where(g => g.UserId == userId && g.RevokedAt == null)
            .OrderByDescending(g => g.CreatedAt)
            .Select(g => new OAuthGrantDto
            {
                Id = g.Id,
                ClientId = g.ClientId,
                ClientName = g.ClientName,
                CompanyId = g.CompanyId,
                CompanyName = _context.Client.Where(c => c.Id == g.CompanyId).Select(c => c.CompanyName).FirstOrDefault(),
                Scopes = g.Scopes,
                CreatedAt = g.CreatedAt,
                LastUsedAt = g.LastUsedAt
            })
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeGrantForUserAsync(long userId, long grantId, CancellationToken ct = default)
    {
        // UserId is part of the predicate, so someone else's id is indistinguishable from a
        // non-existent one — same pattern as ApiKeyService.RevokeAsync.
        var owned = await _context.OAuthGrant
            .AsNoTracking()
            .AnyAsync(g => g.Id == grantId && g.UserId == userId && g.RevokedAt == null, ct);

        if (!owned)
            return false;

        await RevokeGrantAsync(grantId, EOAuthGrantRevokedReason.User, userId, ct);
        return true;
    }

    // ─── Eligibility (allowlist + Q9) ───────────────────────────────────────

    /// <summary>
    /// ADR §4.7/Q9/§5.1: a user must be active, belong to a company (SysAdmin never gets OAuth),
    /// and be on the allowlist (or AllowAll must be on). Checked at consent, at the token
    /// endpoint (both grant types), and — via <c>ApiKeyAuthenticator</c>'s revocation checks —
    /// implicitly at every subsequent request.
    /// </summary>
    private async Task<bool> IsEligibleAsync(Domain.Entities.User user, long? companyId, CancellationToken ct)
    {
        if (!user.IsActive) return false;
        if (!companyId.HasValue) return false;
        if (user.Role == Domain.Enums.EUserRole.SysAdmin) return false;
        // Membership is authoritative: a missing/revoked membership cannot be recovered
        // from User.CompanyId, which is merely the default for interactive sign-in.
        if (!await _context.UserCompanyMembership.AsNoTracking().AnyAsync(m =>
            m.UserId == user.Id && m.CompanyId == companyId && m.IsActive &&
            m.Company.IsActive && m.Company.IsIssuer &&
            (m.Role == EUserRole.User || m.Role == EUserRole.Admin), ct)) return false;
        if (_options.AllowAll) return true;
        if (_options.AllowedUserIds.Contains(user.Id)) return true;
        return _options.AllowedCompanyIds.Contains(companyId.Value);
    }

    // ─── Token/code generation + validation ─────────────────────────────────

    /// <summary>
    /// Codex review finding (low): the token response must report the access token's ACTUAL
    /// remaining lifetime, not the nominal one hour — <see cref="CreateAccessToken"/> caps
    /// <c>ExpiresAt</c> at the grant's absolute expiry (Q4), and a refresh minted in the last
    /// hour before that boundary would otherwise advertise <c>expires_in=3600</c> for a token
    /// that is actually only good for a few seconds or minutes.
    /// </summary>
    private static int ExpiresInSeconds(ApiKeyEntity accessToken)
        => Math.Max(0, (int)(accessToken.ExpiresAt!.Value - DateTime.UtcNow).TotalSeconds);

    private static (string Raw, ApiKeyEntity Entity) CreateAccessToken(OAuthGrant grant, string scope)
    {
        var raw = AccessTokenPrefix + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        // Capped by the grant's absolute expiry (Q4), same reasoning as CreateRefreshToken below
        // (Codex review finding) — an access token minted in the last hour before the grant's
        // 180-day ceiling must not outlive the grant itself.
        var normalExpiry = DateTime.UtcNow.Add(AccessTokenLifetime);
        var grantExpiresAtUtc = PostgresDateTime.ToUtc(grant.ExpiresAt);
        var expiresAt = normalExpiry < grantExpiresAtUtc ? normalExpiry : grantExpiresAtUtc;

        var entity = new ApiKeyEntity
        {
            UserId = grant.UserId,
            CompanyId = grant.CompanyId,
            AllowedCompanyIds = grant.CompanyId.HasValue ? [grant.CompanyId.Value] : [],
            Name = $"OAuth: {grant.ClientName}",
            KeyPrefix = raw[..12],
            KeyHash = ApiKeyService.ComputeHash(raw),
            Scopes = scope,
            OAuthGrantId = grant.Id,
            ExpiresAt = expiresAt
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
