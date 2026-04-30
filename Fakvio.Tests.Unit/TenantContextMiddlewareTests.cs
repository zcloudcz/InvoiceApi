using System.Security.Claims;
using Fakvio.API.Middleware;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for TenantContextMiddleware.
///
/// This middleware validates that authenticated users have a valid, provisioned,
/// and active tenant for tenant-scoped API endpoints. It skips validation for
/// master-only paths (/api/auth, /api/user, /api/company, /swagger, /health).
///
/// We test:
/// - Master-only paths are skipped (no tenant check needed)
/// - Non-API paths are skipped
/// - Unauthenticated users pass through (let [Authorize] handle it)
/// - SysAdmin without impersonation gets 403 (must use X-Company-Id header)
/// - Missing CompanyId → 403
/// - Missing CompanySystemSettings → 403
/// - Unprovisioned tenant → 403
/// - Inactive tenant → 403
/// - Valid tenant → passes through
/// </summary>
public class TenantContextMiddlewareTests : IDisposable
{
    private readonly MasterDbContext _masterContext;
    private readonly ILogger<TenantContextMiddleware> _logger;

    public TenantContextMiddlewareTests()
    {
        // In-memory MasterDbContext — used by middleware to check CompanySystemSettings
        var options = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _masterContext = new MasterDbContext(options);
        _logger = Substitute.For<ILogger<TenantContextMiddleware>>();
    }

    /// <summary>
    /// Creates an HttpContext with the given configuration.
    /// Sets up request path, user claims, and a service provider that
    /// resolves MasterDbContext (needed by the middleware to check tenant status).
    /// </summary>
    private HttpContext CreateHttpContext(
        string path,
        string? role = null,
        string? companyId = null,
        bool isAuthenticated = true)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        if (isAuthenticated && role != null)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "TestUser"),
                new Claim(ClaimTypes.Email, "test@example.com"),
                new Claim(ClaimTypes.Role, role)
            };

            if (companyId != null)
            {
                claims.Add(new Claim("CompanyId", companyId));
            }

            var identity = new ClaimsIdentity(claims, "TestAuth");
            context.User = new ClaimsPrincipal(identity);
        }

        // Set up a service provider so the middleware can resolve ITenantDbContextFactory and TenantDbContext.
        // The middleware delegates tenant resolution to the factory (single source of truth).
        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var configData = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=test;Password=test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configData).Build();

        var services = new ServiceCollection();
        services.AddSingleton(_masterContext);
        services.AddScoped(_ => new TenantDbContext(tenantOptions));
        services.AddSingleton<ITenantResolver>(Substitute.For<ITenantResolver>());
        services.AddSingleton<ITenantProvisioningService>(Substitute.For<ITenantProvisioningService>());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(Substitute.For<ILogger<TenantDbContextFactory>>());
        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();
        context.RequestServices = services.BuildServiceProvider();

        // Set up response body stream for WriteAsJsonAsync
        context.Response.Body = new MemoryStream();

        return context;
    }

    /// <summary>
    /// Seeds a CompanySystemSettings record in the in-memory MasterDbContext.
    /// </summary>
    private async Task SeedSettingsAsync(
        long companyId,
        bool isProvisioned = true,
        bool isActive = true)
    {
        // Seed the company (Client with IsIssuer = true)
        _masterContext.Client.Add(new Client
        {
            Id = companyId,
            CompanyName = $"Company {companyId}",
            RegistrationNumber = $"REG{companyId:D8}",
            IsIssuer = true,
            IsActive = true,
            Address = new List<Address>(),
            Contact = new List<Contact>()
        });
        await _masterContext.SaveChangesAsync();

        // Seed the settings
        _masterContext.CompanySystemSettings.Add(new CompanySystemSettings
        {
            CompanyId = companyId,
            SchemaName = $"tenant_{companyId}",
            IsProvisioned = isProvisioned,
            IsActive = isActive,
            ProvisionedAt = isProvisioned ? DateTime.UtcNow : null
        });
        await _masterContext.SaveChangesAsync();
    }

    /// <summary>
    /// Helper to create the middleware with a tracking delegate for _next.
    /// Returns (middleware, wasCalled flag).
    /// </summary>
    private TenantContextMiddleware CreateMiddleware(out bool[] nextCalled)
    {
        var called = new bool[] { false };
        nextCalled = called;
        return new TenantContextMiddleware(
            _ => { called[0] = true; return Task.CompletedTask; },
            _logger);
    }

    #region Path Skipping Tests

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/refresh")]
    [InlineData("/api/user/invite")]
    [InlineData("/api/company")]
    [InlineData("/api/company/42")]
    [InlineData("/api/sysadmin/payment-matching/settings")]
    [InlineData("/api/sysadmin/payment-matching/run-now")]
    [InlineData("/api/dashboard/sysadmin")]
    [InlineData("/api/logs/paged")]
    [InlineData("/api/system-configuration")]
    [InlineData("/api/twofactor/setup")]
    [InlineData("/api/cloud-storage/connect")]
    [InlineData("/api/email/send")]
    [InlineData("/swagger")]
    [InlineData("/swagger/v1/swagger.json")]
    [InlineData("/health")]
    public async Task MasterOnlyPaths_SkipTenantValidation(string path)
    {
        // Arrange — authenticated user with role but no CompanyId
        var context = CreateHttpContext(path, role: "Admin");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — should pass through without tenant check
        nextCalled[0].ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(200);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/home")]
    [InlineData("/some-page")]
    public async Task NonApiPaths_SkipTenantValidation(string path)
    {
        // Arrange — any request to a non-API path
        var context = CreateHttpContext(path, role: "User");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled[0].ShouldBeTrue();
    }

    #endregion

    #region Authentication Bypass Tests

    [Fact]
    public async Task UnauthenticatedUser_PassesThrough()
    {
        // Arrange — unauthenticated request to a tenant endpoint
        var context = CreateHttpContext("/api/invoice", isAuthenticated: false);
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — let [Authorize] attribute handle it, not the middleware
        nextCalled[0].ShouldBeTrue();
    }

    #endregion

    #region SysAdmin Without Impersonation Tests

    [Fact]
    public async Task SysAdmin_WithoutCompanyId_Returns403()
    {
        // Arrange — SysAdmin without impersonation (no CompanyId claim).
        // SysAdmin MUST use X-Company-Id header to specify the target tenant.
        // Without it, there's no tenant database to query.
        var context = CreateHttpContext("/api/invoice", role: "SysAdmin");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — SysAdmin without impersonation is blocked with 403
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task SysAdmin_WithoutCompanyId_MasterOnlyPath_PassesThrough()
    {
        // Arrange — SysAdmin accessing master-only endpoints (no CompanyId needed).
        // Paths like /api/company, /api/user, /api/auth skip tenant validation entirely.
        var context = CreateHttpContext("/api/company", role: "SysAdmin");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — master-only paths skip tenant check regardless of role
        nextCalled[0].ShouldBeTrue();
    }

    /// <summary>
    /// Regression for issue #34: chat endpoints are tenant-scoped (data lives in
    /// TenantDbContext.ChatConversation). SysAdmin without a CompanyId claim has no
    /// tenant to read from, so the middleware must reject these calls with 403.
    ///
    /// The Blazor UI hides the chat icon and drawer entirely (via the _hasTenantContext
    /// flag in MainLayout.razor) when SysAdmin has not impersonated a company, so this
    /// 403 is a defense-in-depth backstop — it must not be reachable from a real user
    /// session, but the API still rejects the call if it ever arrives.
    ///
    /// If this test starts failing, either (a) chat endpoints have moved to a
    /// master-only path (then the UI guard in MainLayout.razor can be relaxed too),
    /// or (b) a regression is letting SysAdmin hit tenant-scoped chat data without
    /// a tenant, which is incorrect.
    /// </summary>
    [Theory]
    [InlineData("/api/chat/conversations")]
    [InlineData("/api/chat/providers")]
    public async Task SysAdmin_WithoutCompanyId_ChatEndpoint_Returns403(string path)
    {
        // Arrange — SysAdmin without impersonation hitting a chat endpoint.
        var context = CreateHttpContext(path, role: "SysAdmin");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — chat endpoints are NOT in MasterOnlyPaths, so the middleware
        // returns 403 with the impersonation hint. The UI guards the chat icon and
        // drawer behind the _hasTenantContext flag (Fakvio.UI.Shared/Components/Layout/MainLayout.razor),
        // so this 403 should never be surfaced to a real user — but we pin the
        // backend behavior so a regression can't silently expose tenant data.
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    /// <summary>
    /// Parity guard between API and Functions middleware: every API master-only path
    /// MUST also be a master-only path on the Functions host, and vice versa. When the
    /// two lists drift, the same endpoint behaves differently on each host — exactly
    /// the bug from issue #34 where /api/sysadmin/payment-matching/settings worked on
    /// the API host but returned 403 on Functions because the Functions middleware was
    /// missing the prefix.
    ///
    /// Drift in either direction is dangerous:
    /// - API has prefix, Functions doesn't → endpoint silently 403s on Azure deploys.
    /// - Functions has prefix, API doesn't → API treats endpoint as tenant-scoped
    ///   (good), Functions skips tenant validation → potential auth bypass on Azure.
    ///
    /// Read both private static arrays via reflection so the test follows the source
    /// of truth without duplicating the lists.
    /// </summary>
    [Fact]
    public void MasterOnlyPaths_ApiAndFunctions_AreInSync()
    {
        var apiPaths = ReadMasterOnlyArray(
            typeof(Fakvio.API.Middleware.TenantContextMiddleware),
            "MasterOnlyPaths");
        var functionsPaths = ReadMasterOnlyArray(
            typeof(Fakvio.Functions.Middleware.TenantContextMiddleware),
            "MasterOnlyPrefixes");

        // Host-only paths that don't apply to the Functions host (no Swagger UI, no
        // health endpoint served by the Worker). Allowed to be API-only.
        var apiOnlyAllowList = new HashSet<string> { "/swagger", "/health" };

        var missingFromFunctions = apiPaths
            .Where(p => !apiOnlyAllowList.Contains(p) && !functionsPaths.Contains(p))
            .ToList();

        missingFromFunctions.ShouldBeEmpty(
            "Every API master-only path (except /swagger and /health, which the " +
            "Functions host does not serve) must also be on the Functions middleware's " +
            "MasterOnlyPrefixes list. Drift here causes endpoints to silently 403 on " +
            "Azure deploys while passing on the API host (issue #34).");

        // Reverse direction — a Functions-only prefix would let Azure skip tenant
        // validation for an endpoint the API host treats as tenant-scoped.
        var missingFromApi = functionsPaths
            .Where(p => !apiPaths.Contains(p))
            .ToList();

        missingFromApi.ShouldBeEmpty(
            "Every Functions master-only prefix must also be on the API middleware's " +
            "MasterOnlyPaths list. A Functions-only prefix is an auth-bypass risk: the " +
            "Azure host would skip tenant validation for an endpoint the API host " +
            "correctly treats as tenant-scoped.");
    }

    private static IReadOnlyList<string> ReadMasterOnlyArray(Type middlewareType, string fieldName)
    {
        var field = middlewareType.GetField(
            fieldName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        field.ShouldNotBeNull(
            $"Expected private static field '{fieldName}' on {middlewareType.FullName}. " +
            "If it was renamed, update this parity test accordingly.");
        var value = field!.GetValue(null) as string[];
        value.ShouldNotBeNull($"{middlewareType.FullName}.{fieldName} must be a string[].");
        return value!;
    }

    #endregion

    #region Missing CompanyId Tests

    [Fact]
    public async Task RegularUser_WithoutCompanyId_Returns403()
    {
        // Arrange — regular user without CompanyId claim
        var context = CreateHttpContext("/api/invoice", role: "User");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — should block with 403
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task AdminUser_WithInvalidCompanyId_Returns403()
    {
        // Arrange — admin user with non-numeric CompanyId
        var context = CreateHttpContext("/api/invoice", role: "Admin", companyId: "not-a-number");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — should block with 403
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    #endregion

    #region Tenant Status Tests

    [Fact]
    public async Task ValidTenant_ProvisionedAndActive_PassesThrough()
    {
        // Arrange — company with fully provisioned and active tenant
        await SeedSettingsAsync(companyId: 42, isProvisioned: true, isActive: true);
        var context = CreateHttpContext("/api/invoice", role: "User", companyId: "42");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — should pass through
        nextCalled[0].ShouldBeTrue();
    }

    [Fact]
    public async Task MissingSettings_Returns403()
    {
        // Arrange — company exists but no CompanySystemSettings record
        // (CompanyId 999 has no settings seeded)
        var context = CreateHttpContext("/api/invoice", role: "User", companyId: "999");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task UnprovisionedTenant_Returns403()
    {
        // Arrange — settings exist but database not provisioned yet
        await SeedSettingsAsync(companyId: 50, isProvisioned: false, isActive: true);
        var context = CreateHttpContext("/api/invoice", role: "User", companyId: "50");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task InactiveTenant_Returns403()
    {
        // Arrange — settings exist, provisioned, but deactivated
        await SeedSettingsAsync(companyId: 60, isProvisioned: true, isActive: false);
        var context = CreateHttpContext("/api/invoice", role: "User", companyId: "60");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task SysAdmin_WithImpersonation_ValidTenant_PassesThrough()
    {
        // Arrange — SysAdmin impersonating company 42 (has valid tenant)
        await SeedSettingsAsync(companyId: 42, isProvisioned: true, isActive: true);
        var context = CreateHttpContext("/api/invoice", role: "SysAdmin", companyId: "42");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — impersonating SysAdmin with valid tenant should pass
        nextCalled[0].ShouldBeTrue();
    }

    [Fact]
    public async Task SysAdmin_WithImpersonation_InactiveTenant_Returns403()
    {
        // Arrange — SysAdmin impersonating company 70 (tenant is inactive)
        await SeedSettingsAsync(companyId: 70, isProvisioned: true, isActive: false);
        var context = CreateHttpContext("/api/invoice", role: "SysAdmin", companyId: "70");
        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — even SysAdmin is blocked when impersonating an inactive tenant
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(403);
    }

    #endregion

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }
}
