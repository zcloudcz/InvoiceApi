using System.Security.Claims;
using InvoiceApi.API.Middleware;
using InvoiceApi.Domain.Entities;
using InvoiceApi.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace InvoiceApi.Tests.Unit;

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

        // Set up a service provider so the middleware can resolve MasterDbContext
        var services = new ServiceCollection();
        services.AddSingleton(_masterContext);
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
