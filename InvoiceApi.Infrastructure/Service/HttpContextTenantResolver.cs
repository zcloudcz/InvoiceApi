using System.Security.Claims;
using InvoiceApi.Application.Service;
using Microsoft.AspNetCore.Http;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Resolves the current tenant (company) from HttpContext JWT claims.
///
/// How it works:
/// 1. Normal user logs in → AuthService adds "CompanyId" claim to JWT
/// 2. SysAdmin impersonates → ImpersonationMiddleware adds "CompanyId" claim from X-Company-Id header
/// 3. This resolver reads the "CompanyId" claim from either source
///
/// Returns null when:
/// - No HttpContext exists (background jobs, console tools)
/// - User is not authenticated
/// - No CompanyId claim present (SysAdmin without impersonation)
/// </summary>
public class HttpContextTenantResolver : ITenantResolver
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextTenantResolver(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public long? GetCurrentCompanyId()
    {
        // Read the "CompanyId" custom claim from JWT.
        // This is a custom claim (string literal "CompanyId"), NOT ClaimTypes.CompanyId.
        // Set by AuthService.GenerateJwtTokenAsync for normal users,
        // or by ImpersonationMiddleware for SysAdmin impersonation.
        var companyIdClaim = _httpContextAccessor.HttpContext?.User
            ?.FindFirst("CompanyId")?.Value;

        if (long.TryParse(companyIdClaim, out var companyId))
        {
            return companyId;
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsSysAdmin()
    {
        // Check the Role claim for "SysAdmin" value.
        // SysAdmin users have no CompanyId in their JWT — they must impersonate
        // via X-Company-Id header to access tenant data.
        var roleClaim = _httpContextAccessor.HttpContext?.User
            ?.FindFirst(ClaimTypes.Role)?.Value;

        return roleClaim == "SysAdmin";
    }
}
