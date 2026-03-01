using System.Security.Claims;
using Fakvio.API.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for ImpersonationMiddleware.
/// Verifies that SysAdmin users can impersonate companies via X-Company-Id header,
/// and that non-SysAdmin users cannot use this feature.
/// </summary>
public class ImpersonationMiddlewareTests
{
    private readonly ILogger<ImpersonationMiddleware> _logger;

    public ImpersonationMiddlewareTests()
    {
        _logger = Substitute.For<ILogger<ImpersonationMiddleware>>();
    }

    /// <summary>
    /// Creates a test HttpContext with the given claims and optional X-Company-Id header.
    /// </summary>
    private static HttpContext CreateHttpContext(
        string? role = null,
        string? companyId = null,
        string? headerCompanyId = null,
        bool isAuthenticated = true)
    {
        var context = new DefaultHttpContext();

        if (isAuthenticated && role != null)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "TestUser"),
                new Claim(ClaimTypes.Role, role)
            };

            if (companyId != null)
            {
                claims.Add(new Claim("CompanyId", companyId));
            }

            var identity = new ClaimsIdentity(claims, "TestAuth");
            context.User = new ClaimsPrincipal(identity);
        }

        if (headerCompanyId != null)
        {
            context.Request.Headers["X-Company-Id"] = headerCompanyId;
        }

        return context;
    }

    [Fact]
    public async Task SysAdmin_WithHeader_AddsCompanyIdClaim()
    {
        // Arrange: SysAdmin sends X-Company-Id: 42
        var context = CreateHttpContext(role: "SysAdmin", headerCompanyId: "42");
        var nextCalled = false;
        var middleware = new ImpersonationMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            _logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert: CompanyId claim should be set to 42
        nextCalled.ShouldBeTrue();
        var companyIdClaim = context.User.FindFirst("CompanyId")?.Value;
        companyIdClaim.ShouldBe("42");
    }

    [Fact]
    public async Task SysAdmin_WithoutHeader_NoCompanyIdClaim()
    {
        // Arrange: SysAdmin without X-Company-Id header
        var context = CreateHttpContext(role: "SysAdmin");
        var middleware = new ImpersonationMiddleware(
            _ => Task.CompletedTask,
            _logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert: No CompanyId claim should exist
        var companyIdClaim = context.User.FindFirst("CompanyId");
        companyIdClaim.ShouldBeNull();
    }

    [Fact]
    public async Task AdminUser_WithHeader_IgnoresHeader()
    {
        // Arrange: Admin user tries to send X-Company-Id (should be ignored)
        var context = CreateHttpContext(role: "Admin", companyId: "10", headerCompanyId: "42");
        var middleware = new ImpersonationMiddleware(
            _ => Task.CompletedTask,
            _logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert: CompanyId should remain the original value (10), not the header (42)
        var companyIdClaim = context.User.FindFirst("CompanyId")?.Value;
        companyIdClaim.ShouldBe("10");
    }

    [Fact]
    public async Task RegularUser_WithHeader_IgnoresHeader()
    {
        // Arrange: Regular user tries to send X-Company-Id (should be ignored)
        var context = CreateHttpContext(role: "User", companyId: "5", headerCompanyId: "99");
        var middleware = new ImpersonationMiddleware(
            _ => Task.CompletedTask,
            _logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert: CompanyId should remain the original value (5)
        var companyIdClaim = context.User.FindFirst("CompanyId")?.Value;
        companyIdClaim.ShouldBe("5");
    }

    [Fact]
    public async Task SysAdmin_WithInvalidHeader_NoCompanyIdClaim()
    {
        // Arrange: SysAdmin sends invalid (non-numeric) X-Company-Id
        var context = CreateHttpContext(role: "SysAdmin", headerCompanyId: "not-a-number");
        var middleware = new ImpersonationMiddleware(
            _ => Task.CompletedTask,
            _logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert: No CompanyId claim should be added
        var companyIdClaim = context.User.FindFirst("CompanyId");
        companyIdClaim.ShouldBeNull();
    }

    [Fact]
    public async Task UnauthenticatedUser_WithHeader_IgnoresHeader()
    {
        // Arrange: Unauthenticated request with X-Company-Id header
        var context = CreateHttpContext(isAuthenticated: false);
        context.Request.Headers["X-Company-Id"] = "42";
        var middleware = new ImpersonationMiddleware(
            _ => Task.CompletedTask,
            _logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert: No claims should be added
        context.User.Identity?.IsAuthenticated.ShouldBeFalse();
    }

    [Fact]
    public async Task SysAdmin_WithEmptyHeader_NoCompanyIdClaim()
    {
        // Arrange: SysAdmin sends empty X-Company-Id
        var context = CreateHttpContext(role: "SysAdmin", headerCompanyId: "");
        var middleware = new ImpersonationMiddleware(
            _ => Task.CompletedTask,
            _logger);

        // Act
        await middleware.InvokeAsync(context);

        // Assert: No CompanyId claim should be added
        var companyIdClaim = context.User.FindFirst("CompanyId");
        companyIdClaim.ShouldBeNull();
    }
}
