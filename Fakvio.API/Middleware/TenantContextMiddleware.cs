using System.Security.Claims;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Middleware;

/// <summary>
/// Middleware that validates tenant context for tenant-scoped API endpoints.
///
/// For endpoints that require a tenant database (invoices, clients, templates, etc.),
/// this middleware verifies:
/// 1. The user has a CompanyId claim (set by JWT or impersonation middleware)
/// 2. The company has a provisioned and active tenant database
///
/// Endpoints that DON'T need a tenant are skipped:
/// - /api/auth (login, password management — uses master DB)
/// - /api/user (user management — uses master DB)
/// - /api/company (company management — uses master DB, SysAdmin only)
/// - /swagger, /health, etc.
///
/// Must be registered in the pipeline AFTER UseAuthentication, UseAuthorization,
/// and UseImpersonation (so the CompanyId claim is already set).
/// </summary>
public class TenantContextMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantContextMiddleware> _logger;

    /// <summary>
    /// Path prefixes that are master-only and do NOT require a tenant context.
    /// These endpoints operate on the master database (users, auth, companies).
    /// </summary>
    private static readonly string[] MasterOnlyPaths =
    [
        "/api/auth",
        "/api/user",
        "/api/company",
        "/api/system-configuration", // SMTP + JWT settings — master DB, SysAdmin only
        "/api/dashboard/sysadmin",   // SysAdmin dashboard — master DB, no tenant needed
        "/api/logs",                 // Application logs — master DB, SysAdmin only
        "/api/twofactor",            // 2FA setup/verify — operates on master DB User table
        "/api/cloud-storage",        // Cloud storage settings — stored in master DB CompanySystemSettings
        "/swagger",
        "/health"
    ];

    /// <summary>
    /// Path prefixes for code table endpoints that SysAdmin can access WITHOUT impersonation.
    /// These services use dual-context: MasterDbContext when no tenant, TenantDbContext when impersonating.
    /// Regular users still go through normal tenant validation for these paths.
    /// </summary>
    private static readonly string[] SysAdminCodeTablePaths =
    [
        "/api/currency",
        "/api/vatrate",
        "/api/contenttemplate",
        "/api/numbersequence/formats" // Only formats — actual sequences are tenant-only
    ];

    public TenantContextMiddleware(RequestDelegate next, ILogger<TenantContextMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Skip tenant validation for master-only endpoints and non-API paths
        if (!path.StartsWith("/api/") || IsMasterOnlyPath(path))
        {
            await _next(context);
            return;
        }

        // Skip for unauthenticated requests — let the [Authorize] attribute handle it
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        // Extract role and CompanyId claims for tenant resolution.
        // CompanyId is set by JWT (regular users) or ImpersonationMiddleware (SysAdmin).
        var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
        var companyIdClaim = context.User.FindFirst("CompanyId")?.Value;

        // SysAdmin without impersonation: allow access to code table endpoints
        // (currencies, VAT rates, content templates, number sequences).
        // These services use dual-context — they automatically switch to MasterDbContext
        // when no tenant is available, allowing SysAdmin to manage global code tables.
        if (roleClaim == "SysAdmin" && string.IsNullOrEmpty(companyIdClaim))
        {
            if (IsSysAdminCodeTablePath(path))
            {
                _logger.LogDebug(
                    "SysAdmin accessing code table endpoint {Path} without impersonation — using MasterDbContext",
                    path);
                await _next(context);
                return;
            }

            _logger.LogWarning(
                "SysAdmin tried to access tenant endpoint {Path} without impersonation. " +
                "Use X-Company-Id header to specify the target tenant.",
                path);

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "SysAdmin must impersonate a company to access tenant endpoints. " +
                          "Add the X-Company-Id header with the target company ID."
            });
            return;
        }

        // Regular user or impersonating SysAdmin — must have a valid CompanyId
        if (string.IsNullOrEmpty(companyIdClaim) || !long.TryParse(companyIdClaim, out var companyId))
        {
            _logger.LogWarning("Tenant access denied: no CompanyId claim for user {User}",
                context.User.FindFirst(ClaimTypes.Email)?.Value ?? "unknown");

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Access denied. No company context found. Contact your administrator."
            });
            return;
        }

        // Verify the tenant is provisioned and active in master DB
        var masterContext = context.RequestServices.GetRequiredService<MasterDbContext>();
        var tenantSettings = await masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);

        if (tenantSettings == null)
        {
            _logger.LogWarning("Tenant access denied: no CompanySystemSettings for company {CompanyId}", companyId);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Company is not configured for multi-tenant access. Contact your administrator."
            });
            return;
        }

        if (!tenantSettings.IsProvisioned)
        {
            _logger.LogWarning("Tenant access denied: company {CompanyId} is not provisioned", companyId);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Company database has not been provisioned yet. Contact your administrator."
            });
            return;
        }

        if (!tenantSettings.IsActive)
        {
            _logger.LogWarning("Tenant access denied: company {CompanyId} is deactivated", companyId);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Company account is currently deactivated. Contact your administrator."
            });
            return;
        }

        // Tenant is valid — set the Schema on the DI-resolved TenantDbContext
        // so all EF Core queries are routed to the correct tenant schema (e.g., "tenant_1").
        // Without this, TenantDbContext has no schema set and queries target the "public" schema,
        // causing "relation does not exist" errors for tenant-only tables.
        var tenantContext = context.RequestServices.GetRequiredService<TenantDbContext>();
        tenantContext.Schema = tenantSettings.SchemaName;

        await _next(context);
    }

    /// <summary>
    /// Checks if the request path is a master-only endpoint that doesn't need tenant validation.
    /// </summary>
    private static bool IsMasterOnlyPath(string path)
    {
        return MasterOnlyPaths.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the request path is a code table endpoint that SysAdmin can access
    /// without impersonation. These services use dual-context (MasterDbContext fallback).
    /// </summary>
    private static bool IsSysAdminCodeTablePath(string path)
    {
        return SysAdminCodeTablePaths.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Extension method to register the tenant context middleware in the pipeline.
/// Must be called AFTER UseAuthentication(), UseAuthorization(), and UseImpersonation().
/// </summary>
public static class TenantContextMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantContext(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TenantContextMiddleware>();
    }
}
