using System.Net;
using System.Net.Http.Json;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Contracts.Dto.Auth;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.CompanySettings;
using Fakvio.Infrastructure.Data;
using Fakvio.Tests.Integration.Fixtures;
using Fakvio.Tests.Integration.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Fakvio.Tests.Integration;

/// <summary>
/// Integration tests for API-key authentication in the API host (issue #236).
///
/// These run the REAL pipeline — policy scheme selector → ApiKeyAuthenticationHandler →
/// ApiKeyScopeMiddleware → ImpersonationMiddleware → TenantContextMiddleware → controller —
/// which is the only way to prove the claims an API key produces are interchangeable with
/// the JWT ones. A unit test on the authenticator cannot see any of that wiring.
///
/// The Functions host mirror is covered by ApiKeyFunctionsMiddlewareTests (unit): the
/// isolated worker has no in-process test server, and both hosts drive the same
/// IApiKeyAuthenticator + ApiKeyRequestGuard.
/// </summary>
public class ApiKeyAuthenticationEndpointTests : IClassFixture<FakvioFactory>
{
    private const string TenantUserEmail = "api-key-auth-test@example.com";
    private const long TenantUserId = 300;
    private const string CompanyName = "API Key Auth Test Co";
    private const string CompanyRegistrationNumber = "11223399";

    private readonly FakvioFactory _factory;
    private readonly string _tenantUserPassword;
    private readonly long _companyId;

    public ApiKeyAuthenticationEndpointTests(FakvioFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
        _tenantUserPassword = _factory.SeedRegularUser(TenantUserEmail, TenantUserId);

        _companyId = ProvisionCompanyAsync().GetAwaiter().GetResult();
        AttachUserToCompany(TenantUserId, _companyId);
    }

    // ─── Arrange helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Creates + provisions a company through the real SysAdmin endpoints, so that
    /// TenantContextMiddleware lets tenant-scoped requests through.
    ///
    /// Idempotent: xUnit builds a new test class instance per test while the factory (and
    /// therefore the InMemory database) is shared, so this runs many times against the
    /// same data and must reuse what is already there.
    /// </summary>
    private async Task<long> ProvisionCompanyAsync()
    {
        var existingId = FindCompanyId();
        if (existingId is not null)
            return existingId.Value;

        var client = await CreateJwtClientAsSysAdminAsync();

        var created = await client.PostAsJsonAsync("/api/company", new CreateClientDto
        {
            CompanyName = CompanyName,
            RegistrationNumber = CompanyRegistrationNumber,
            IsIssuer = true,
            FetchFromAres = false
        });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var company = (await created.Content.ReadFromJsonAsync<ClientDto>())!;

        var settings = await client.PostAsJsonAsync("/api/company/settings", new CreateCompanySystemSettingsDto
        {
            CompanyId = company.Id,
            SchemaName = $"test_tenant_{company.Id}"
        });
        settings.StatusCode.ShouldBe(HttpStatusCode.Created);

        var provisioned = await client.PostAsync($"/api/company/{company.Id}/provision", null);
        provisioned.StatusCode.ShouldBe(HttpStatusCode.OK);

        return company.Id;
    }

    private long? FindCompanyId()
    {
        using var scope = _factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

        return master.Client
            .Where(c => c.RegistrationNumber == CompanyRegistrationNumber && c.IsIssuer)
            .Select(c => (long?)c.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// SeedRegularUser creates a company-less user; the whole point of these tests is a key
    /// whose tenant is derived from User.CompanyId, so wire the two together.
    /// </summary>
    private void AttachUserToCompany(long userId, long companyId)
    {
        using var scope = _factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var user = master.User.First(u => u.Id == userId);
        user.CompanyId = companyId;
        if (!master.UserCompanyMembership.Any(m => m.UserId == userId && m.CompanyId == companyId))
            master.UserCompanyMembership.Add(new Fakvio.Domain.Entities.UserCompanyMembership { UserId = userId, CompanyId = companyId, Role = user.Role });
        master.SaveChanges();
    }

    private async Task<HttpClient> CreateJwtClientAsSysAdminAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsSysAdminAsync(client);
        AuthHelper.SetAuthToken(client, login.Token);
        return client;
    }

    private async Task<HttpClient> CreateJwtClientAsTenantUserAsync()
    {
        var client = _factory.CreateClient();
        var login = await AuthHelper.LoginAsync(client, TenantUserEmail, _tenantUserPassword);
        AuthHelper.SetAuthToken(client, login.Token);
        return client;
    }

    /// <summary>
    /// Mints a key through the real POST /api/api-key endpoint (JWT-authenticated) and
    /// returns a client that presents it — exactly what a machine client does.
    /// </summary>
    private async Task<(HttpClient Client, CreatedApiKeyDto Key)> CreateApiKeyClientAsync(
        HttpClient ownerClient, string scopes = "read")
    {
        var response = await ownerClient.PostAsJsonAsync("/api/api-key", new CreateApiKeyDto
        {
            Name = $"test key {Guid.NewGuid():N}",
            Scopes = scopes
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = (await response.Content.ReadFromJsonAsync<CreatedApiKeyDto>())!;

        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, created.Key);
        return (client, created);
    }

    /// <summary>
    /// Ages a key past its expiry. POST /api/api-key refuses to mint one already expired,
    /// which is exactly the state under test.
    /// </summary>
    private void ExpireKey(long keyId)
    {
        using var scope = _factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<MasterDbContext>();
        var key = master.ApiKey.First(k => k.Id == keyId);
        key.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        master.SaveChanges();
    }

    // ─── Happy path + tenant scoping ─────────────────────────────────────────

    [Fact]
    public async Task ValidKey_ReachesATenantScopedEndpoint()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(owner);

        // /api/client is tenant-scoped: getting 200 proves the CompanyId claim the
        // authenticator built carried all the way through TenantContextMiddleware.
        var response = await apiKeyClient.GetAsync("/api/client");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidKey_MeReportsTheIdentityAndScopes()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, created) = await CreateApiKeyClientAsync(owner, scopes: "read,write");

        var response = await apiKeyClient.GetAsync("/api/api-key/me");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var identity = (await response.Content.ReadFromJsonAsync<ApiKeyIdentityDto>())!;
        identity.UserId.ShouldBe(TenantUserId);
        identity.CompanyId.ShouldBe(_companyId);
        identity.Scopes.ShouldBe("read,write");
        identity.ApiKeyId.ShouldBe(created.Id);
    }

    [Fact]
    public async Task JwtSession_StillWorksOnTheSameEndpoints()
    {
        // Regression guard: the default scheme is now a selector, not JwtBearer directly.
        var owner = await CreateJwtClientAsTenantUserAsync();

        (await owner.GetAsync("/api/client")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var me = await owner.GetAsync("/api/api-key/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        var identity = (await me.Content.ReadFromJsonAsync<ApiKeyIdentityDto>())!;
        // A JWT session is not scope-limited, so the key-specific fields stay empty.
        identity.Scopes.ShouldBeNull();
        identity.ApiKeyId.ShouldBeNull();
    }

    // ─── Fail closed ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokedKey_Returns401()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, created) = await CreateApiKeyClientAsync(owner);

        var revoked = await owner.PostAsync($"/api/api-key/{created.Id}/revoke", null);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await apiKeyClient.GetAsync("/api/client")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredKey_Returns401()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, created) = await CreateApiKeyClientAsync(owner);
        ExpireKey(created.Id);

        (await apiKeyClient.GetAsync("/api/client")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownKey_Returns401()
    {
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, "fak_live_" + new string('A', 43));

        (await client.GetAsync("/api/client")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GarbageBearerToken_StillReturns401ThroughTheJwtBranch()
    {
        // Not "fak_" prefixed, so the selector sends it to JWT — which must keep behaving
        // exactly as it did before the selector existed.
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, "not-a-token-at-all");

        (await client.GetAsync("/api/client")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeadKey_DoesNotBreakAnAnonymousEndpoint()
    {
        // Locates the 401 the tests above see. The handler runs on this request too (the
        // selector forwards any "fak_" bearer to it) and refuses the key — yet the request
        // is served. So the 401 is produced by the authorization challenge on a PROTECTED
        // endpoint, not by the handler: all the handler decides is whether a principal
        // exists. That is the same fail-closed-but-not-fail-loud behaviour the Functions
        // host spells out in ApiKeyAuthenticationMiddleware, and it is what keeps a client
        // with a stale key in its config able to log in and fetch a fresh one.
        var client = _factory.CreateClient();
        AuthHelper.SetAuthToken(client, "fak_live_" + new string('A', 43));

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = TenantUserEmail,
            Password = _tenantUserPassword
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ─── Scopes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadOnlyKey_IsForbiddenOnAWriteEndpoint()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(owner, scopes: "read");

        var response = await apiKeyClient.PostAsJsonAsync("/api/client", new CreateClientDto
        {
            CompanyName = "Should never be created",
            RegistrationNumber = "00000001",
            FetchFromAres = false
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadWriteKey_MayWrite()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(owner, scopes: "read,write");

        var response = await apiKeyClient.PostAsJsonAsync("/api/client", new CreateClientDto
        {
            CompanyName = "Created by a write-scoped key",
            RegistrationNumber = "00000002",
            FetchFromAres = false
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ReadOnlyKey_MayPostToASafeMethodOverridePath()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(owner, scopes: "read");

        var response = await apiKeyClient.PostAsJsonAsync("/api/tax/estimate", new
        {
            grossIncome = 1_000_000m,
            taxRegime = "FlatRate"
        });

        // The estimate may well answer 400 for a tenant with no tax config — what matters
        // is that the scope guard did not stop it, which a plain POST would have.
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);
    }

    // ─── Key management stays JWT-only ───────────────────────────────────────

    [Fact]
    public async Task ApiKey_CannotListKeys()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(owner, scopes: "read,write");

        (await apiKeyClient.GetAsync("/api/api-key")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ApiKey_CannotMintAnotherKey()
    {
        var owner = await CreateJwtClientAsTenantUserAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(owner, scopes: "read,write");

        var response = await apiKeyClient.PostAsJsonAsync("/api/api-key", new CreateApiKeyDto
        {
            Name = "bootstrapped by a key",
            Scopes = "read,write"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ─── SysAdmin impersonation (story #144 approved default) ────────────────

    [Fact]
    public async Task SysAdminKey_WithCompanyHeader_ImpersonatesTheTenant()
    {
        // The approved default of story #144: a SysAdmin key carries the owner's role, so
        // X-Company-Id impersonation works with a key exactly as it does with a JWT.
        var sysAdmin = await CreateJwtClientAsSysAdminAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(sysAdmin);
        AuthHelper.SetImpersonation(apiKeyClient, _companyId);

        (await apiKeyClient.GetAsync("/api/client")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SysAdminKey_WithoutCompanyHeader_IsRefusedOnTenantEndpoints()
    {
        // Companion to the test above: impersonation is what grants the tenant, not the
        // key. Without the header TenantContextMiddleware answers 403, same as for a JWT.
        var sysAdmin = await CreateJwtClientAsSysAdminAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(sysAdmin);

        (await apiKeyClient.GetAsync("/api/client")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SysAdminKey_SatisfiesRoleBasedAuthorization()
    {
        // [Authorize(Roles = "SysAdmin")] only passes when the identity was built with
        // roleType: ClaimTypes.Role — the same trap issue #20 hit on the JWT path.
        var sysAdmin = await CreateJwtClientAsSysAdminAsync();
        var (apiKeyClient, _) = await CreateApiKeyClientAsync(sysAdmin);

        var response = await apiKeyClient.GetAsync("/api/sysadmin/payment-matching/settings");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
