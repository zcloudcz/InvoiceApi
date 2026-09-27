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
            Options.Create(new McpOAuthOptions { ResourceProofSecret = ResourceProofSecret }));

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
            UserId = UserId, ClientId = "https://claude.ai/oauth/claude-code-client-metadata",
            ClientName = "Claude Code", Scopes = "read", Resource = CanonicalResource,
            ExpiresAt = DateTime.UtcNow.AddDays(180)
        };
        _context.OAuthGrant.Add(grant);
        _context.SaveChanges();

        _context.ApiKey.Add(new ApiKey
        {
            UserId = UserId, Name = "OAuth: Claude Code", KeyPrefix = RawToken[..12],
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
            UserId = UserId, Name = "Manual key", KeyPrefix = rawKey[..12],
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
}
