using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Authentication;
using Fakvio.Infrastructure.Authentication.OAuth;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the OAuth-specific half of <see cref="ApiKeyAuthenticator"/> (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md §4.4 threat T6, §4.5/§4.7): the resource-proof header gate on
/// <c>fak_oat_…</c> tokens, and the extra claims (<c>oauth_grant_id</c>, <c>oauth_resource</c>)
/// those tokens carry. Runs on InMemory — nothing here touches the atomic
/// ExecuteUpdate/ExecuteDelete operations that require a real PostgreSQL (see
/// Fakvio.Tests.Integration/OAuthServiceTests.cs for those).
/// </summary>
public class ApiKeyAuthenticatorOAuthTests : IDisposable
{
    private const long UserId = 1;
    private const string ResourceProofSecret = "test-resource-proof-secret";
    private const string CanonicalResource = "https://mcp.fakvio.cz/mcp";

    private readonly string _databaseName = Guid.NewGuid().ToString();
    private readonly MasterDbContext _context;
    private readonly ApiKeyAuthenticator _authenticator;

    public ApiKeyAuthenticatorOAuthTests()
    {
        _context = new MasterDbContext(new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options);

        _authenticator = new ApiKeyAuthenticator(
            _context,
            Substitute.For<ILogger<ApiKeyAuthenticator>>(),
            Options.Create(new McpOAuthOptions { Enabled = true, AllowAll = true, ResourceProofSecret = ResourceProofSecret }));

        _context.Client.Add(new Client { Id = 10, RegistrationNumber = "10", IsIssuer = true });
        _context.User.Add(new User
        {
            Id = UserId, Email = "owner@test.cz", FirstName = "Test", LastName = "User",
            Role = EUserRole.User, CompanyId = 10, IsActive = true, IsEmailVerified = true,
            ExternalProvider = EExternalProvider.None
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>Inserts an OAuth-issued access token row directly — mirrors what OAuthService produces.</summary>
    private const string RawToken = "fak_oat_test-token-0123456789";

    private void SeedOAuthAccessToken()
    {
        var grant = new OAuthGrant
        {
            UserId = UserId, CompanyId = 10, ClientId = "https://claude.ai/oauth/claude-code-client-metadata",
            ClientName = "Claude Code", Scopes = "read", Resource = CanonicalResource,
            ExpiresAt = DateTime.UtcNow.AddDays(180)
        };
        _context.OAuthGrant.Add(grant);
        _context.SaveChanges();

        _context.ApiKey.Add(new ApiKey
        {
            UserId = UserId, CompanyId = 10, AllowedCompanyIds = [10], Name = "OAuth: Claude Code", KeyPrefix = RawToken[..12],
            KeyHash = ApiKeyService.ComputeHash(RawToken), Scopes = "read",
            OAuthGrantId = grant.Id, ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task OAuthToken_WithoutProofHeader_IsRejected()
    {
        SeedOAuthAccessToken();

        (await _authenticator.AuthenticateAsync(RawToken, resourceProofHeader: null)).ShouldBeNull();
    }

    [Fact]
    public async Task OAuthToken_RevokedCompanyMembershipIsRejected()
    {
        SeedOAuthAccessToken();
        (await _context.UserCompanyMembership.SingleAsync(m => m.UserId == UserId)).IsActive = false;
        await _context.SaveChangesAsync();
        (await _authenticator.AuthenticateAsync(RawToken, ResourceProofSecret)).ShouldBeNull();
    }

    [Fact]
    public async Task OAuthToken_InactiveCompanyIsRejected()
    {
        SeedOAuthAccessToken();
        (await _context.Client.SingleAsync()).IsActive = false;
        await _context.SaveChangesAsync();
        (await _authenticator.AuthenticateAsync(RawToken, ResourceProofSecret)).ShouldBeNull();
    }

    [Fact]
    public async Task OAuthToken_MismatchedGrantCompanyIsRejected()
    {
        SeedOAuthAccessToken();
        (await _context.OAuthGrant.SingleAsync()).CompanyId = 20;
        await _context.SaveChangesAsync();
        (await _authenticator.AuthenticateAsync(RawToken, ResourceProofSecret)).ShouldBeNull();
    }

    [Fact]
    public async Task OAuthToken_RemovedFromAllowlistIsRejected()
    {
        SeedOAuthAccessToken();
        var restricted = new ApiKeyAuthenticator(_context, Substitute.For<ILogger<ApiKeyAuthenticator>>(),
            Options.Create(new McpOAuthOptions { Enabled = true, ResourceProofSecret = ResourceProofSecret }));
        (await restricted.AuthenticateAsync(RawToken, ResourceProofSecret)).ShouldBeNull();
    }

    [Fact]
    public async Task OAuthToken_WithWrongProofSecret_IsRejected()
    {
        SeedOAuthAccessToken();

        (await _authenticator.AuthenticateAsync(RawToken, resourceProofHeader: "wrong-secret")).ShouldBeNull();
    }

    [Fact]
    public async Task OAuthToken_WithCorrectProofSecret_Authenticates()
    {
        SeedOAuthAccessToken();

        var principal = await _authenticator.AuthenticateAsync(RawToken, resourceProofHeader: ResourceProofSecret);

        principal.ShouldNotBeNull();
        principal.FindFirst(ApiKeyAuthenticationDefaults.OAuthGrantIdClaimType).ShouldNotBeNull();
        principal.FindFirst(ApiKeyAuthenticationDefaults.OAuthResourceClaimType)!.Value.ShouldBe(CanonicalResource);
    }

    [Fact]
    public async Task ManuallyCreatedKey_NeedsNoProofHeader()
    {
        // A plain "fak_live_…" key (OAuthGrantId null) must keep working exactly as before —
        // the proof-header requirement is strictly additional, only for OAuth-issued tokens.
        const string rawKey = "fak_live_test-manual-key-0123456789";
        _context.ApiKey.Add(new ApiKey
        {
            UserId = UserId, CompanyId = 10, AllowedCompanyIds = [10], Name = "Manual key", KeyPrefix = rawKey[..12],
            KeyHash = ApiKeyService.ComputeHash(rawKey), Scopes = "read"
        });
        _context.SaveChanges();

        var principal = await _authenticator.AuthenticateAsync(rawKey, resourceProofHeader: null);

        principal.ShouldNotBeNull();
        principal.FindFirst(ApiKeyAuthenticationDefaults.OAuthGrantIdClaimType).ShouldBeNull();
    }

    [Fact]
    public async Task OAuthToken_WhenSecretNotConfigured_IsRejectedEvenWithAHeader()
    {
        // Fail closed: an unconfigured secret must not be treated as "no proof required".
        SeedOAuthAccessToken();

        var authenticatorWithoutSecret = new ApiKeyAuthenticator(
            _context, Substitute.For<ILogger<ApiKeyAuthenticator>>(), Options.Create(new McpOAuthOptions()));

        (await authenticatorWithoutSecret.AuthenticateAsync(RawToken, resourceProofHeader: "anything")).ShouldBeNull();
    }

    /// <summary>
    /// Codex review finding (critical, T4/T6/T15): the ADR's "quick rollback"
    /// (McpOAuth:Enabled=false) must terminate every OAuth-issued token immediately, not just
    /// hide the discovery/token endpoints. Before this fix, a still-unexpired fak_oat_ token
    /// kept authenticating after the flag flip as long as the resource-proof secret stayed
    /// configured — this pins that the rollback actually works.
    /// </summary>
    [Fact]
    public async Task OAuthToken_WhenMcpOAuthDisabled_IsRejectedEvenWithACorrectProofHeader()
    {
        SeedOAuthAccessToken();

        var authenticatorWithFlagOff = new ApiKeyAuthenticator(
            _context, Substitute.For<ILogger<ApiKeyAuthenticator>>(),
            Options.Create(new McpOAuthOptions { Enabled = false, ResourceProofSecret = ResourceProofSecret }));

        (await authenticatorWithFlagOff.AuthenticateAsync(RawToken, resourceProofHeader: ResourceProofSecret)).ShouldBeNull();
    }

    /// <summary>Belt for the suspenders in <see cref="OAuthCleanupService"/>/<c>RevokeGrantAsync</c>'s sweep: the grant's own state is authoritative too.</summary>
    [Fact]
    public async Task OAuthToken_WhoseGrantIsRevoked_IsRejected_EvenIfTheAccessTokenRowItselfIsNot()
    {
        var grant = new OAuthGrant
        {
            UserId = UserId, CompanyId = 10, ClientId = "https://claude.ai/oauth/claude-code-client-metadata",
            ClientName = "Claude Code", Scopes = "read", Resource = CanonicalResource,
            ExpiresAt = DateTime.UtcNow.AddDays(180), RevokedAt = DateTime.UtcNow, RevokedReason = EOAuthGrantRevokedReason.User
        };
        _context.OAuthGrant.Add(grant);
        _context.SaveChanges();

        // Deliberately NOT revoked at the ApiKey row level — simulates the row existing
        // between RevokeGrantAsync's two statements (grant, then the ApiKey sweep), or any
        // future code path that revokes a grant without remembering to sweep its tokens.
        _context.ApiKey.Add(new ApiKey
        {
            UserId = UserId, CompanyId = 10, AllowedCompanyIds = [10], Name = "OAuth: Claude Code", KeyPrefix = RawToken[..12],
            KeyHash = ApiKeyService.ComputeHash(RawToken), Scopes = "read",
            OAuthGrantId = grant.Id, ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        _context.SaveChanges();

        (await _authenticator.AuthenticateAsync(RawToken, resourceProofHeader: ResourceProofSecret)).ShouldBeNull();
    }
}
