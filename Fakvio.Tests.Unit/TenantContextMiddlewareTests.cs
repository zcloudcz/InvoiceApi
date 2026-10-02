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
using Npgsql;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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

        // Build a real NpgsqlDataSource for DI — TenantDbContextFactory needs it
        // to derive per-tenant data sources. Sealed class, cannot be mocked.
        var testDataSource = new NpgsqlDataSourceBuilder(
            "Host=localhost;Database=test;Username=test;Password=test").Build();

        var services = new ServiceCollection();
        services.AddSingleton(_masterContext);
        services.AddScoped(_ => new TenantDbContext(tenantOptions));
        services.AddSingleton<ITenantResolver>(Substitute.For<ITenantResolver>());
        services.AddSingleton<ITenantProvisioningService>(Substitute.For<ITenantProvisioningService>());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(testDataSource);
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
    [InlineData("/api/diagnostic/health")]
    [InlineData("/api/vies/CZ12345678")]
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

    #region Migration Failure (HTTP 503) Tests

    /// <summary>
    /// Creates an HttpContext where ITenantDbContextFactory is a mock — allowing us
    /// to control exactly what ResolveSchemaAsync and EnsureMigratedAsync return/throw.
    ///
    /// Used for the 503 migration-failure tests: the real TenantDbContextFactory delegates
    /// to ITenantProvisioningService internally, which makes it hard to inject a failure
    /// purely via DI. Using a mock factory isolates the middleware behaviour from the
    /// factory internals.
    /// </summary>
    private HttpContext CreateHttpContextWithMockFactory(
        string path,
        string? role,
        string companyId,
        ITenantDbContextFactory mockFactory)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "TestUser"),
            new(ClaimTypes.Email, "test@example.com"),
            new(ClaimTypes.Role, role ?? "User"),
            new("CompanyId", companyId)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        context.User = new ClaimsPrincipal(identity);

        var tenantOptions = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var services = new ServiceCollection();
        services.AddScoped(_ => new TenantDbContext(tenantOptions));
        // Use the caller-supplied mock factory so we can control its behaviour
        services.AddSingleton(mockFactory);
        context.RequestServices = services.BuildServiceProvider();

        context.Response.Body = new MemoryStream();

        return context;
    }

    /// <summary>
    /// When EnsureMigratedAsync throws (migration failure — e.g., PostgreSQL permission
    /// error, column already exists from a partially-applied migration), the middleware
    /// must return HTTP 503 "schema not ready" and NOT call _next.
    ///
    /// This is the primary new behaviour introduced by issue #99: before the fix, the
    /// exception was swallowed inside EnsureMigratedAsync, the request proceeded with an
    /// unmigrated schema, and EF Core crashed later with a confusing PostgresException.
    /// After the fix, the middleware intercepts the re-thrown exception and returns 503
    /// so the client gets an actionable message and the call is retryable.
    /// </summary>
    [Fact]
    public async Task MigrationFailure_Returns503_AndDoesNotCallNext()
    {
        // Arrange — mock factory whose ResolveSchemaAsync succeeds but EnsureMigratedAsync throws
        var mockFactory = Substitute.For<ITenantDbContextFactory>();
        mockFactory.ResolveSchemaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns("tenant_42");
        mockFactory.EnsureMigratedAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(
                "42703: column \"VatRegime\" of relation \"InvoiceItem\" does not exist"));

        var context = CreateHttpContextWithMockFactory(
            path: "/api/invoice/paged",
            role: "User",
            companyId: "42",
            mockFactory: mockFactory);

        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — 503 returned, pipeline halted
        nextCalled[0].ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// HTTP 503 response body must contain a "message" field so the client
    /// gets an actionable explanation rather than an empty 503.
    /// </summary>
    [Fact]
    public async Task MigrationFailure_Response503_ContainsMessage()
    {
        // Arrange
        var mockFactory = Substitute.For<ITenantDbContextFactory>();
        mockFactory.ResolveSchemaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns("tenant_42");
        mockFactory.EnsureMigratedAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Simulated migration failure"));

        var context = CreateHttpContextWithMockFactory(
            path: "/api/invoice",
            role: "Admin",
            companyId: "42",
            mockFactory: mockFactory);

        var middleware = CreateMiddleware(out _);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — response body contains JSON with a "message" key
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        body.ShouldContain("message");
        // Body should mention "migration" so the admin knows what happened (503 must be diagnosable)
        body.ShouldContain("migration");
    }

    /// <summary>
    /// When EnsureMigratedAsync succeeds (normal hot path), the middleware must still
    /// call _next — the 503 guard must not interfere with happy-path requests.
    /// </summary>
    [Fact]
    public async Task MigrationSuccess_CallsNext_Returns200()
    {
        // Arrange — factory where both ResolveSchemaAsync and EnsureMigratedAsync succeed
        var mockFactory = Substitute.For<ITenantDbContextFactory>();
        mockFactory.ResolveSchemaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns("tenant_42");
        mockFactory.EnsureMigratedAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var context = CreateHttpContextWithMockFactory(
            path: "/api/invoice/paged",
            role: "User",
            companyId: "42",
            mockFactory: mockFactory);

        var middleware = CreateMiddleware(out var nextCalled);

        // Act
        await middleware.InvokeAsync(context);

        // Assert — _next was called; 200 is the default when _next doesn't set a code
        nextCalled[0].ShouldBeTrue();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
    }

    #endregion

    public void Dispose()
    {
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }
}
