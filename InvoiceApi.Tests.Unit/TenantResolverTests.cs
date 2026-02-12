using System.Security.Claims;
using Shouldly;
using InvoiceApi.Infrastructure.Service;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace InvoiceApi.Tests.Unit;

/// <summary>
/// Tests for HttpContextTenantResolver — resolves CompanyId and SysAdmin status from JWT claims.
///
/// Test scenarios:
/// - Normal user with CompanyId claim → returns CompanyId
/// - SysAdmin without impersonation → returns null CompanyId, IsSysAdmin = true
/// - SysAdmin with impersonation (CompanyId injected by middleware) → returns impersonated CompanyId
/// - Anonymous/unauthenticated request → returns null for both
/// - No HttpContext (background jobs) → returns null for both
/// </summary>
public class TenantResolverTests
{
    /// <summary>
    /// Creates a mock IHttpContextAccessor with the given claims.
    /// This simulates what happens after JWT authentication and ImpersonationMiddleware.
    /// </summary>
    private static IHttpContextAccessor CreateMockAccessor(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);
        var context = new DefaultHttpContext { User = principal };

        var mock = Substitute.For<IHttpContextAccessor>();
        mock.HttpContext.Returns(context);
        return mock;
    }

    /// <summary>
    /// Creates a mock IHttpContextAccessor with no authentication (anonymous).
    /// </summary>
    private static IHttpContextAccessor CreateAnonymousAccessor()
    {
        var context = new DefaultHttpContext();
        var mock = Substitute.For<IHttpContextAccessor>();
        mock.HttpContext.Returns(context);
        return mock;
    }

    /// <summary>
    /// Creates a mock IHttpContextAccessor with null HttpContext (background job scenario).
    /// </summary>
    private static IHttpContextAccessor CreateNullContextAccessor()
    {
        var mock = Substitute.For<IHttpContextAccessor>();
        mock.HttpContext.Returns((HttpContext?)null);
        return mock;
    }

    [Fact]
    public void GetCurrentCompanyId_NormalUserWithCompanyClaim_ReturnsCompanyId()
    {
        // Arrange — normal user (e.g., accountant) with CompanyId = 42 in JWT
        var accessor = CreateMockAccessor(
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "User"),
            new Claim("CompanyId", "42")
        );
        var resolver = new HttpContextTenantResolver(accessor);

        // Act
        var companyId = resolver.GetCurrentCompanyId();

        // Assert
        companyId.ShouldBe(42);
    }

    [Fact]
    public void GetCurrentCompanyId_SysAdminWithoutImpersonation_ReturnsNull()
    {
        // Arrange — SysAdmin has no CompanyId claim (they don't belong to any company)
        var accessor = CreateMockAccessor(
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "SysAdmin")
        );
        var resolver = new HttpContextTenantResolver(accessor);

        // Act
        var companyId = resolver.GetCurrentCompanyId();

        // Assert
        companyId.ShouldBeNull();
    }

    [Fact]
    public void GetCurrentCompanyId_SysAdminWithImpersonation_ReturnsImpersonatedCompanyId()
    {
        // Arrange — SysAdmin impersonating company 99 (ImpersonationMiddleware injected CompanyId)
        var accessor = CreateMockAccessor(
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "SysAdmin"),
            new Claim("CompanyId", "99")
        );
        var resolver = new HttpContextTenantResolver(accessor);

        // Act
        var companyId = resolver.GetCurrentCompanyId();

        // Assert — should return the impersonated company ID
        companyId.ShouldBe(99);
    }

    [Fact]
    public void GetCurrentCompanyId_AnonymousRequest_ReturnsNull()
    {
        // Arrange — no authentication (anonymous endpoint)
        var accessor = CreateAnonymousAccessor();
        var resolver = new HttpContextTenantResolver(accessor);

        // Act
        var companyId = resolver.GetCurrentCompanyId();

        // Assert
        companyId.ShouldBeNull();
    }

    [Fact]
    public void GetCurrentCompanyId_NoHttpContext_ReturnsNull()
    {
        // Arrange — no HttpContext (background job, console tool)
        var accessor = CreateNullContextAccessor();
        var resolver = new HttpContextTenantResolver(accessor);

        // Act
        var companyId = resolver.GetCurrentCompanyId();

        // Assert
        companyId.ShouldBeNull();
    }

    [Fact]
    public void IsSysAdmin_SysAdminRole_ReturnsTrue()
    {
        // Arrange
        var accessor = CreateMockAccessor(
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "SysAdmin")
        );
        var resolver = new HttpContextTenantResolver(accessor);

        // Act & Assert
        resolver.IsSysAdmin().ShouldBeTrue();
    }

    [Fact]
    public void IsSysAdmin_NormalUserRole_ReturnsFalse()
    {
        // Arrange
        var accessor = CreateMockAccessor(
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "User"),
            new Claim("CompanyId", "42")
        );
        var resolver = new HttpContextTenantResolver(accessor);

        // Act & Assert
        resolver.IsSysAdmin().ShouldBeFalse();
    }

    [Fact]
    public void IsSysAdmin_NoRoleClaim_ReturnsFalse()
    {
        // Arrange — authenticated but no role claim
        var accessor = CreateMockAccessor(
            new Claim(ClaimTypes.NameIdentifier, "1")
        );
        var resolver = new HttpContextTenantResolver(accessor);

        // Act & Assert
        resolver.IsSysAdmin().ShouldBeFalse();
    }

    [Fact]
    public void IsSysAdmin_NoHttpContext_ReturnsFalse()
    {
        // Arrange
        var accessor = CreateNullContextAccessor();
        var resolver = new HttpContextTenantResolver(accessor);

        // Act & Assert
        resolver.IsSysAdmin().ShouldBeFalse();
    }

    [Fact]
    public void GetCurrentCompanyId_InvalidCompanyIdClaim_ReturnsNull()
    {
        // Arrange — CompanyId claim has non-numeric value (shouldn't happen, but defensive)
        var accessor = CreateMockAccessor(
            new Claim(ClaimTypes.NameIdentifier, "1"),
            new Claim(ClaimTypes.Role, "User"),
            new Claim("CompanyId", "not-a-number")
        );
        var resolver = new HttpContextTenantResolver(accessor);

        // Act
        var companyId = resolver.GetCurrentCompanyId();

        // Assert
        companyId.ShouldBeNull();
    }
}
