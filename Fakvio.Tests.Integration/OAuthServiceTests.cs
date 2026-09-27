using System.Net.Sockets;
using System.Security.Cryptography;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication.OAuth;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="OAuthService"/> against a REAL PostgreSQL schema (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md, task N5.3) — same reasoning as
/// <see cref="OAuthDatabaseConstraintTests"/>: the service's whole security model rests on
/// atomic <c>ExecuteUpdateAsync</c> "consume once" races, which the InMemory EF Core provider
/// cannot even execute (it throws <c>NotSupportedException</c>), let alone prove atomic.
///
/// Every phase (arrange / act / assert) opens its OWN <see cref="MasterDbContext"/>, exactly
/// like <see cref="ApiKeyDatabaseConstraintTests"/> — reusing one context across a raw-SQL
/// tamper and a subsequent service call would read back the SAME tracked in-memory entity
/// instead of what is actually in the database (EF Core's identity map), silently hiding the
/// very tampering the test is trying to exercise.
///
/// Covers threats T3 (code reuse), T5 (refresh reuse) with the Q5 grace window, T11 (tenant/
/// SysAdmin escalation), T12 (scope escalation), and the resource/redirect_uri/client_id binding
/// checks that back T2/T6.
/// </summary>
[Collection(RealPostgreSqlCollection.Name)]
public class OAuthServiceTests : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=fakvio;Username=fakvio;Password=fakvio_dev;Timeout=5";

    private const string ConnectionStringEnvVar = "FAKVIO_TEST_POSTGRES";

    private const string SkipReason =
        "PostgreSQL is not reachable — start it with 'docker compose up -d' " +
        "or point " + ConnectionStringEnvVar + " at another instance.";

    private const long OwnerUserId = 502_001L;
    private const long OwnerCompanyId = 502_100L;
    private const string ClientId = "https://claude.ai/oauth/claude-code-client-metadata";
    private const string RedirectUri = "https://claude.ai/api/mcp/auth_callback";
    private const string Resource = "https://mcp.fakvio.cz/mcp";

    private readonly string _schemaName = $"test_oauthsvc_{Guid.NewGuid():N}"[..40];
    private readonly string _connectionString =
        Environment.GetEnvironmentVariable(ConnectionStringEnvVar) ?? DefaultConnectionString;

    private NpgsqlDataSourceFactory? _dataSourceFactory;
    private bool _databaseAvailable;

    public async Task InitializeAsync()
    {
        _dataSourceFactory = new NpgsqlDataSourceFactory(new DatabaseOptions
        {
            ConnectionString = _connectionString,
            AuthMode = DatabaseAuthMode.Password
        });

        _databaseAvailable = await CanReachPostgreSqlAsync();
        if (!_databaseAvailable)
            return;

        await using (var createSchema = _dataSourceFactory.Root.CreateCommand($"CREATE SCHEMA \"{_schemaName}\""))
            await createSchema.ExecuteNonQueryAsync();

        await using var context = CreateMasterContext();
        await context.Database.MigrateAsync();
        await SeedOwnerAsync();
    }

    private async Task<bool> CanReachPostgreSqlAsync()
    {
        try
        {
            await using var connection = await _dataSourceFactory!.Root.OpenConnectionAsync();
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or SocketException)
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_databaseAvailable && _dataSourceFactory is not null)
        {
            await using var command = _dataSourceFactory.Root.CreateCommand(
                $"DROP SCHEMA IF EXISTS \"{_schemaName}\" CASCADE");
            await command.ExecuteNonQueryAsync();
        }

        if (_dataSourceFactory is not null)
            await _dataSourceFactory.DisposeAsync();
    }

    private MasterDbContext CreateMasterContext()
        => new(new DbContextOptionsBuilder<MasterDbContext>()
            .UseNpgsql(_dataSourceFactory!.GetForSchema(_schemaName, includePublicInSearchPath: false))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options);

    private async Task SeedOwnerAsync()
    {
        await using var context = CreateMasterContext();

        // User.CompanyId carries a real FK to Client on PostgreSQL (enforced, unlike InMemory) —
        // a company row must exist before a user can reference it.
        context.Client.Add(new Client
        {
            Id = OwnerCompanyId,
            CompanyName = "OAuth Test s.r.o.",
            RegistrationNumber = "50200100",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true
        });

        context.User.Add(new User
        {
            Id = OwnerUserId,
            Email = "oauth-svc-owner@fakvio.test",
            PasswordHash = "not-a-real-hash",
            FirstName = "OAuth",
            LastName = "Owner",
            Role = EUserRole.User,
            CompanyId = OwnerCompanyId,
            IsActive = true,
            IsEmailVerified = true,
            ExternalProvider = EExternalProvider.None
        });
        await context.SaveChangesAsync();
    }

    private static OAuthService CreateService(MasterDbContext context, McpOAuthOptions? options = null)
        => new(context, Options.Create(options ?? DefaultOptions()), NullLogger<OAuthService>.Instance);

    private static McpOAuthOptions DefaultOptions() => new()
    {
        Enabled = true,
        AllowAll = true,
        Resource = Resource,
        Issuer = "https://api.fakvio.cz"
    };

    // ─── PKCE helpers ────────────────────────────────────────────────────────

    private static string NewCodeVerifier() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string ChallengeFor(string verifier)
        => Base64UrlEncode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Issues a code exactly the way the consent decision endpoint (N5.4) will — in its OWN
    /// context, so the caller always exchanges it through a fresh one (see class docs).
    /// </summary>
    private async Task<(string Code, string Verifier)> IssueCodeAsync(string scopes = "read")
    {
        await using var context = CreateMasterContext();
        var verifier = NewCodeVerifier();
        var code = await CreateService(context).IssueAuthorizationCodeAsync(
            new IssueAuthorizationCodeRequest(OwnerUserId, ClientId, "Claude Code", RedirectUri, ChallengeFor(verifier), scopes, Resource));
        return (code, verifier);
    }

    /// <summary>Issues a code and immediately redeems it, each through its own context.</summary>
    private async Task<OAuthTokenResult> IssueAndExchangeAsync(string scopes = "read")
    {
        var (code, verifier) = await IssueCodeAsync(scopes);
        await using var context = CreateMasterContext();
        return await CreateService(context).ExchangeAuthorizationCodeAsync(
            new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, verifier, Resource));
    }

    // ─── Authorization code exchange (T2, T3, T6) ───────────────────────────

    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_HappyPath_IssuesTokens()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var (code, verifier) = await IssueCodeAsync();

        await using var context = CreateMasterContext();
        var result = await CreateService(context).ExchangeAuthorizationCodeAsync(
            new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, verifier, Resource));

        result.AccessToken.ShouldStartWith("fak_oat_");
        result.RefreshToken.ShouldNotBeNullOrEmpty();
        // ~3600, not exactly — ExpiresInSeconds computes the token's ACTUAL remaining lifetime
        // at response time (Codex review round 2), so a few milliseconds of test execution
        // between minting the token and this assertion are expected to shave a little off.
        result.ExpiresIn.ShouldBeInRange(3595, 3600);
        result.Scope.ShouldBe("read");
    }

    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_ReusedCode_RevokesTheGrantItProduced()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var (code, verifier) = await IssueCodeAsync();
        var request = new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, verifier, Resource);

        await using (var firstContext = CreateMasterContext())
            await CreateService(firstContext).ExchangeAuthorizationCodeAsync(request);

        await using (var secondContext = CreateMasterContext())
        {
            var act = () => CreateService(secondContext).ExchangeAuthorizationCodeAsync(request);
            var ex = await Should.ThrowAsync<OAuthErrorException>(act);
            ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidGrant);
        }

        // T3: reuse must revoke the grant the FIRST (legitimate) redemption produced —
        // the access token from that first exchange must stop working.
        await using var readContext = CreateMasterContext();
        var grant = await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId);
        grant.RevokedAt.ShouldNotBeNull();
        grant.RevokedReason.ShouldBe(EOAuthGrantRevokedReason.CodeReuse);

        var accessToken = await readContext.ApiKey.SingleAsync(k => k.OAuthGrantId == grant.Id);
        accessToken.RevokedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// Fires the SAME code redemption from two separate DbContexts (i.e. two separate
    /// connections, exactly like two real concurrent HTTP requests) at once, instead of
    /// sequentially. Pins the fix for the Codex review finding: the `FOR UPDATE` row lock in
    /// <see cref="OAuthService.ExchangeAuthorizationCodeAsync"/> must serialize the two
    /// attempts so the loser ALWAYS observes the winner's final <c>GrantId</c> — never the
    /// in-between state where <c>ConsumedAt</c> is set but <c>GrantId</c> is still null, which
    /// would make the reuse it detects impossible to revoke.
    /// </summary>
    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_TrueConcurrentRedemption_AlwaysRevokesTheWinningGrant()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var (code, verifier) = await IssueCodeAsync();
        var request = new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, verifier, Resource);

        async Task<OAuthErrorException?> RedeemAsync()
        {
            await using var context = CreateMasterContext();
            try
            {
                await CreateService(context).ExchangeAuthorizationCodeAsync(request);
                return null; // this attempt won
            }
            catch (OAuthErrorException ex)
            {
                return ex; // this attempt lost (or something else went wrong)
            }
        }

        var results = await Task.WhenAll(RedeemAsync(), RedeemAsync());

        // Exactly one of the two must have won (returned null) and the other must have lost
        // with invalid_grant — never both winning, never both losing.
        var losers = results.Where(ex => ex is not null).ToList();
        losers.Count.ShouldBe(1);
        losers[0]!.ErrorCode.ShouldBe(OAuthErrorException.InvalidGrant);

        await using var readContext = CreateMasterContext();
        var grant = await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId);

        // The whole point of the fix: the loser must have been able to find and revoke the
        // grant the winner produced — not silently do nothing because GrantId was still null.
        grant.RevokedAt.ShouldNotBeNull();
        grant.RevokedReason.ShouldBe(EOAuthGrantRevokedReason.CodeReuse);

        var accessToken = await readContext.ApiKey.SingleAsync(k => k.OAuthGrantId == grant.Id);
        accessToken.RevokedAt.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_WrongCodeVerifier_IsRejected()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var (code, _) = await IssueCodeAsync();

        await using var context = CreateMasterContext();
        var act = () => CreateService(context).ExchangeAuthorizationCodeAsync(
            new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, NewCodeVerifier(), Resource));

        var ex = await Should.ThrowAsync<OAuthErrorException>(act);
        ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidGrant);
    }

    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_MismatchedRedirectUri_IsRejected()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var (code, verifier) = await IssueCodeAsync();

        await using var context = CreateMasterContext();
        var act = () => CreateService(context).ExchangeAuthorizationCodeAsync(
            new ExchangeAuthorizationCodeRequest(code, "https://evil.example.com/callback", ClientId, verifier, Resource));

        var ex = await Should.ThrowAsync<OAuthErrorException>(act);
        ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidGrant);
    }

    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_WrongResource_IsRejectedAsInvalidTarget()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var (code, verifier) = await IssueCodeAsync();

        await using var context = CreateMasterContext();
        var act = () => CreateService(context).ExchangeAuthorizationCodeAsync(
            new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, verifier, "https://not-mcp.fakvio.cz/mcp"));

        var ex = await Should.ThrowAsync<OAuthErrorException>(act);
        ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidTarget);
    }

    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_ExpiredCode_IsRejected()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var (code, verifier) = await IssueCodeAsync();

        // Force the code into the past — 60s TTL (ADR §4.2). Own context, and a fresh one
        // below for the exchange, so the exchange reads the tampered row, not a stale
        // in-memory copy (see class docs).
        await using (var tamperContext = CreateMasterContext())
        {
            await tamperContext.Database.ExecuteSqlAsync(
                $"UPDATE \"OAuthAuthorizationCode\" SET \"ExpiresAt\" = now() - interval '1 minute' WHERE \"UserId\" = {OwnerUserId}");
        }

        await using var context = CreateMasterContext();
        var act = () => CreateService(context).ExchangeAuthorizationCodeAsync(
            new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, verifier, Resource));

        var ex = await Should.ThrowAsync<OAuthErrorException>(act);
        ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidGrant);
    }

    [SkippableFact]
    public async Task ExchangeAuthorizationCodeAsync_Reconnect_SupersedesThePriorGrant()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await IssueAndExchangeAsync();
        await IssueAndExchangeAsync();

        await using var readContext = CreateMasterContext();
        var grants = await readContext.OAuthGrant.Where(g => g.UserId == OwnerUserId).ToListAsync();
        grants.Count.ShouldBe(2);
        grants.Count(g => g.RevokedAt is null).ShouldBe(1);
        grants.Single(g => g.RevokedAt is not null).RevokedReason.ShouldBe(EOAuthGrantRevokedReason.Superseded);
    }

    [SkippableFact]
    public async Task IssueAuthorizationCodeAsync_SysAdminWithoutCompany_IsDenied()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        const long sysAdminId = OwnerUserId + 1;
        await using (var context = CreateMasterContext())
        {
            context.User.Add(new User
            {
                Id = sysAdminId, Email = "sysadmin@fakvio.test", PasswordHash = "x",
                FirstName = "Sys", LastName = "Admin", Role = EUserRole.SysAdmin,
                CompanyId = null, IsActive = true, ExternalProvider = EExternalProvider.None
            });
            await context.SaveChangesAsync();
        }

        await using var context2 = CreateMasterContext();
        var verifier = NewCodeVerifier();

        // Q9: SysAdmin has no company, so IssueAuthorizationCodeAsync itself must refuse —
        // a code must never even be minted for this user.
        var act = () => CreateService(context2).IssueAuthorizationCodeAsync(
            new IssueAuthorizationCodeRequest(sysAdminId, ClientId, "Claude Code", RedirectUri, ChallengeFor(verifier), "read", Resource));

        var ex = await Should.ThrowAsync<OAuthErrorException>(act);
        ex.ErrorCode.ShouldBe(OAuthErrorException.AccessDenied);
    }

    // ─── Refresh rotation, Q5 grace window, T5, T12 ─────────────────────────

    [SkippableFact]
    public async Task RefreshAsync_HappyPath_RotatesToken()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();

        await using var context = CreateMasterContext();
        var result = await CreateService(context).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));

        result.AccessToken.ShouldNotBe(initial.AccessToken);
        result.RefreshToken.ShouldNotBe(initial.RefreshToken);
    }

    [SkippableFact]
    public async Task RefreshAsync_ReuseWithinGraceWindow_FailsWithoutRevokingTheGrant()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();

        // First use rotates the token...
        await using (var firstContext = CreateMasterContext())
            await CreateService(firstContext).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));

        // ...a near-immediate second use of the SAME (now-consumed) token is Q5's benign race.
        await using (var secondContext = CreateMasterContext())
        {
            var act = () => CreateService(secondContext).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));
            var ex = await Should.ThrowAsync<OAuthErrorException>(act);
            ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidGrant);
        }

        await using var readContext = CreateMasterContext();
        var grant = await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId);
        grant.RevokedAt.ShouldBeNull("Q5: reuse within the 10s grace window must not revoke the grant");
    }

    [SkippableFact]
    public async Task RefreshAsync_ReuseAfterGraceWindow_RevokesTheWholeGrant()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();

        await using (var firstContext = CreateMasterContext())
            await CreateService(firstContext).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));

        // Push the (already-consumed) old token's ConsumedAt outside the 10s grace window.
        await using (var tamperContext = CreateMasterContext())
        {
            await tamperContext.Database.ExecuteSqlAsync(
                $"""
                 UPDATE "OAuthRefreshToken" SET "ConsumedAt" = now() - interval '1 minute'
                 WHERE "TokenHash" = {ApiKeyService.ComputeHash(initial.RefreshToken)}
                 """);
        }

        await using (var secondContext = CreateMasterContext())
        {
            var act = () => CreateService(secondContext).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));
            var ex = await Should.ThrowAsync<OAuthErrorException>(act);
            ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidGrant);
        }

        await using var readContext = CreateMasterContext();
        var grant = await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId);
        grant.RevokedAt.ShouldNotBeNull();
        grant.RevokedReason.ShouldBe(EOAuthGrantRevokedReason.RefreshReuse);

        // Every access token under the grant must die with it (T5/T6 — "najednou").
        (await readContext.ApiKey.Where(k => k.OAuthGrantId == grant.Id).AllAsync(k => k.RevokedAt != null)).ShouldBeTrue();
    }

    /// <summary>
    /// Fires a legitimate refresh and a revoke of the SAME grant truly concurrently (separate
    /// DbContexts/connections). Pins the fix for the Codex review finding: the grant row's
    /// `FOR UPDATE` lock in <see cref="OAuthService.RefreshAsync"/> must serialize the two
    /// operations completely, so there is never a moment where the access token refresh just
    /// created survives a revoke that logically "already happened" — whichever operation wins
    /// the race, the other one's effects must be consistent with it, never silently lost.
    /// </summary>
    [SkippableFact]
    public async Task RefreshAsync_TrueConcurrentWithRevoke_NeverLeavesALiveTokenUnderARevokedGrant()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();
        long grantId;
        await using (var context = CreateMasterContext())
            grantId = (await context.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId)).Id;

        async Task RefreshAttemptAsync()
        {
            await using var context = CreateMasterContext();
            try
            {
                await CreateService(context).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));
            }
            catch (OAuthErrorException)
            {
                // Fine — revoke may have won the race and refused this refresh. The assertions
                // below are what actually prove the invariant, not which side "won".
            }
        }

        async Task RevokeAttemptAsync()
        {
            await using var context = CreateMasterContext();
            await CreateService(context).RevokeGrantAsync(grantId, EOAuthGrantRevokedReason.User, null);
        }

        await Task.WhenAll(RefreshAttemptAsync(), RevokeAttemptAsync());

        await using var readContext = CreateMasterContext();
        var grant = await readContext.OAuthGrant.SingleAsync(g => g.Id == grantId);

        // The revoke always "wins" eventually (it has no eligibility/expiry checks to fail),
        // so the grant must end up revoked either way.
        grant.RevokedAt.ShouldNotBeNull();

        // The invariant the fix protects: whatever access tokens exist under this grant right
        // now — including one a concurrent refresh may have just minted — must ALL be revoked.
        // A live token here would mean refresh's insert happened invisibly to revoke's sweep.
        (await readContext.ApiKey.Where(k => k.OAuthGrantId == grantId).AllAsync(k => k.RevokedAt != null))
            .ShouldBeTrue("a refresh that raced with a revoke of the same grant left a live access token behind");
    }

    /// <summary>
    /// Codex review, round 2 finding (high): fires the SAME refresh token from two separate
    /// DbContexts truly concurrently. The round-1 fix locked the GRANT row but loaded the
    /// refresh-token row itself BEFORE waiting on that lock — so two concurrent callers could
    /// each capture their own "not yet consumed" snapshot before either one blocked, and both
    /// would then pass the (stale) ConsumedAt check after acquiring the lock in turn. The
    /// round-2 fix loads the token row only AFTER the grant lock is held. Pins that at most one
    /// of two truly concurrent uses of the same refresh token can ever succeed.
    /// </summary>
    [SkippableFact]
    public async Task RefreshAsync_TrueConcurrentUseOfTheSameToken_OnlyOneEverSucceeds()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();

        async Task<OAuthTokenResult?> AttemptAsync()
        {
            await using var context = CreateMasterContext();
            try
            {
                return await CreateService(context).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));
            }
            catch (OAuthErrorException)
            {
                return null;
            }
        }

        var results = await Task.WhenAll(AttemptAsync(), AttemptAsync());

        // Exactly one of the two truly concurrent uses of the SAME refresh token may succeed —
        // never both (T5: that would be two live token pairs minted from one credential).
        results.Count(r => r is not null).ShouldBe(1);

        // And the reuse the loser triggered must have revoked the grant outright (outside the
        // Q5 grace window is irrelevant here — the two calls race hard enough in practice that
        // the loser's own read happens well after the winner's write completes and commits).
        await using var readContext = CreateMasterContext();
        var grantAfter = await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId);

        // Either the loser was still inside the Q5 grace window (benign, no revoke) or outside
        // it (revoked) — both are correct outcomes of the FIX under test, which is "never two
        // successes", already asserted above. This assertion only guards against a NEW kind of
        // corruption: a grant that is revoked must still show every access token dead with it.
        if (grantAfter.RevokedAt is not null)
        {
            (await readContext.ApiKey.Where(k => k.OAuthGrantId == grantAfter.Id).AllAsync(k => k.RevokedAt != null))
                .ShouldBeTrue();
        }
    }

    [SkippableFact]
    public async Task RefreshAsync_RequestingWiderScopeThanGranted_IsRejected()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync(scopes: "read");

        await using var context = CreateMasterContext();
        var act = () => CreateService(context).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, "read write", null));
        var ex = await Should.ThrowAsync<OAuthErrorException>(act);
        ex.ErrorCode.ShouldBe(OAuthErrorException.InvalidScope);
    }

    [SkippableFact]
    public async Task RefreshAsync_RequestingNarrowerScope_Succeeds()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync(scopes: "read,write");

        await using var context = CreateMasterContext();
        var result = await CreateService(context).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, "read", null));

        result.Scope.ShouldBe("read");
    }

    [SkippableFact]
    public async Task RefreshAsync_DeactivatedUser_RevokesGrantAndFails()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();

        await using (var tamperContext = CreateMasterContext())
        {
            await tamperContext.Database.ExecuteSqlAsync(
                $"""UPDATE "User" SET "IsActive" = false WHERE "Id" = {OwnerUserId}""");
        }

        await using (var context = CreateMasterContext())
        {
            var act = () => CreateService(context).RefreshAsync(new RefreshTokenRequest(initial.RefreshToken, null, null));
            var ex = await Should.ThrowAsync<OAuthErrorException>(act);
            ex.ErrorCode.ShouldBe(OAuthErrorException.AccessDenied);
        }

        await using var readContext = CreateMasterContext();
        (await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId)).RevokedAt.ShouldNotBeNull();
    }

    // ─── Revoke (RFC 7009) ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task RevokeAsync_RefreshToken_RevokesTheWholeGrant()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();

        await using (var context = CreateMasterContext())
            await CreateService(context).RevokeAsync(initial.RefreshToken, "refresh_token");

        await using var readContext = CreateMasterContext();
        (await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId)).RevokedAt.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task RevokeAsync_AccessToken_RevokesOnlyThatToken()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();

        await using (var context = CreateMasterContext())
            await CreateService(context).RevokeAsync(initial.AccessToken, "access_token");

        await using var readContext = CreateMasterContext();
        var grant = await readContext.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId);
        grant.RevokedAt.ShouldBeNull("revoking one access token must not revoke the grant itself");

        var accessToken = await readContext.ApiKey.SingleAsync(k => k.OAuthGrantId == grant.Id);
        accessToken.RevokedAt.ShouldNotBeNull();
    }

    [SkippableFact]
    public async Task RevokeAsync_UnknownToken_DoesNotThrow()
    {
        // RFC 7009 §2.2 — an unknown token is not an error.
        Skip.IfNot(_databaseAvailable, SkipReason);

        await using var context = CreateMasterContext();
        await Should.NotThrowAsync(() => CreateService(context).RevokeAsync("not-a-real-token", null));
    }

    // ─── Q6 — credential change revokes all grants ──────────────────────────

    [SkippableFact]
    public async Task RevokeAllGrantsForUserAsync_RevokesEveryActiveGrant()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await IssueAndExchangeAsync();

        await using (var context = CreateMasterContext())
            await CreateService(context).RevokeAllGrantsForUserAsync(OwnerUserId, EOAuthGrantRevokedReason.User);

        await using var readContext = CreateMasterContext();
        (await readContext.OAuthGrant.Where(g => g.UserId == OwnerUserId).AllAsync(g => g.RevokedAt != null)).ShouldBeTrue();
    }

    // ─── "Připojené aplikace" (N5.7) ─────────────────────────────────────────

    [SkippableFact]
    public async Task GetGrantsAsync_ReturnsOnlyThatUsersLiveGrants()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await IssueAndExchangeAsync();

        // A revoked grant of the same user must not appear either.
        await using (var context = CreateMasterContext())
        {
            var revoked = await context.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId);
            await CreateService(context).RevokeGrantAsync(revoked.Id, EOAuthGrantRevokedReason.User, OwnerUserId);
        }

        await IssueAndExchangeAsync(); // a second, still-live grant

        await using var readContext = CreateMasterContext();
        var grants = await CreateService(readContext).GetGrantsAsync(OwnerUserId);

        grants.Count.ShouldBe(1);
        grants[0].ClientId.ShouldBe(ClientId);
    }

    [SkippableFact]
    public async Task RevokeGrantForUserAsync_ByTheOwner_RevokesTheGrantAndItsTokens()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var initial = await IssueAndExchangeAsync();
        long grantId;
        await using (var context = CreateMasterContext())
            grantId = (await context.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId)).Id;

        bool result;
        await using (var context = CreateMasterContext())
            result = await CreateService(context).RevokeGrantForUserAsync(OwnerUserId, grantId);

        result.ShouldBeTrue();

        await using var readContext = CreateMasterContext();
        (await readContext.OAuthGrant.SingleAsync(g => g.Id == grantId)).RevokedAt.ShouldNotBeNull();
        (await readContext.ApiKey.SingleAsync(k => k.OAuthGrantId == grantId)).RevokedAt.ShouldNotBeNull();
        initial.AccessToken.ShouldNotBeNullOrEmpty(); // sanity: the token this asserts on actually exists
    }

    [SkippableFact]
    public async Task RevokeGrantForUserAsync_BySomeoneElse_ReturnsFalseAndLeavesItActive()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        await IssueAndExchangeAsync();
        long grantId;
        await using (var context = CreateMasterContext())
            grantId = (await context.OAuthGrant.SingleAsync(g => g.UserId == OwnerUserId)).Id;

        bool result;
        await using (var context = CreateMasterContext())
            result = await CreateService(context).RevokeGrantForUserAsync(OwnerUserId + 999, grantId);

        result.ShouldBeFalse();

        await using var readContext = CreateMasterContext();
        (await readContext.OAuthGrant.SingleAsync(g => g.Id == grantId)).RevokedAt.ShouldBeNull();
    }

    // ─── T14 — no raw secret ever reaches the logger ────────────────────────

    /// <summary>
    /// Drives the full happy path (issue → exchange → refresh → revoke) with a logger that
    /// captures every formatted message, then asserts none of them contain the raw
    /// authorization code, access token, or refresh token — only the metadata OAuthService's
    /// log statements are supposed to carry (grant id, client_id, error code).
    /// </summary>
    [SkippableFact]
    public async Task NoOperation_EverLogsARawSecret()
    {
        Skip.IfNot(_databaseAvailable, SkipReason);

        var capturedMessages = new List<string>();
        var capturingLogger = new CapturingLogger(capturedMessages);

        var (code, verifier) = await IssueCodeAsync();

        OAuthTokenResult exchanged;
        await using (var context = CreateMasterContext())
        {
            var service = new OAuthService(context, Options.Create(DefaultOptions()), capturingLogger);
            exchanged = await service.ExchangeAuthorizationCodeAsync(
                new ExchangeAuthorizationCodeRequest(code, RedirectUri, ClientId, verifier, Resource));
        }

        OAuthTokenResult refreshed;
        await using (var context = CreateMasterContext())
        {
            var service = new OAuthService(context, Options.Create(DefaultOptions()), capturingLogger);
            refreshed = await service.RefreshAsync(new RefreshTokenRequest(exchanged.RefreshToken, null, null));
        }

        await using (var context = CreateMasterContext())
        {
            var service = new OAuthService(context, Options.Create(DefaultOptions()), capturingLogger);
            await service.RevokeAsync(refreshed.RefreshToken, "refresh_token");
        }

        var secrets = new[] { code, verifier, exchanged.AccessToken, exchanged.RefreshToken, refreshed.AccessToken, refreshed.RefreshToken };

        foreach (var message in capturedMessages)
        {
            foreach (var secret in secrets)
                message.ShouldNotContain(secret, customMessage: $"log message leaked a raw secret: {message}");
        }

        capturedMessages.ShouldNotBeEmpty("the capturing logger itself must have seen something, or this test proves nothing");
    }

    /// <summary>Minimal <see cref="ILogger{OAuthService}"/> that records every formatted message, for T14.</summary>
    private sealed class CapturingLogger(List<string> messages) : Microsoft.Extensions.Logging.ILogger<OAuthService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
        }
    }
}
