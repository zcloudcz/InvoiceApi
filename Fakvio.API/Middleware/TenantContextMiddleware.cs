using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fakvio.API.Middleware;

/// <summary>
/// Middleware that validates tenant context for tenant-scoped API endpoints.
///
/// For endpoints that require a tenant database (invoices, clients, templates, chat, etc.),
/// this middleware:
/// 1. Reads CompanyId from JWT claims
/// 2. Calls ITenantDbContextFactory.ResolveSchemaAsync to get the schema name
///    (single source of truth — no duplicated MasterDb lookup here)
/// 3. Sets TenantDbContext.Schema so EF Core queries target the correct tenant schema
/// 4. Calls EnsureMigratedAsync to lazily apply pending migrations (cached per schema)
///
/// Endpoints that DON'T need a tenant are skipped:
/// - /api/auth, /api/user, /api/company (master DB only)
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
        "/api/api-key",              // API keys live in master DB (auth must resolve them before the tenant is known)
        "/api/oauth",                // OAuth grants/consent/authorize (ADR 0001) — master DB, same reasoning as API keys
        "/api/company",
        "/api/system-configuration", // SMTP + JWT settings — master DB, SysAdmin only
        "/api/dashboard/sysadmin",   // SysAdmin dashboard — master DB, no tenant needed
        "/api/feedback",             // Central report storage; service validates owner and company.
        "/api/sysadmin/feedback",    // Global inbox must work without tenant impersonation.
        "/api/logs",                 // Application logs — master DB, SysAdmin only
        "/api/twofactor",            // 2FA setup/verify — operates on master DB User table
        "/api/cloud-storage",        // Cloud storage settings — stored in master DB CompanySystemSettings
        "/api/email",                // SysAdmin email — uses system SMTP, no tenant needed
        "/api/sysadmin/payment-matching", // Payment matching IMAP/poll config — master DB, SysAdmin only
        "/api/diagnostic",           // Deployment diagnostics — master DB only; a SysAdmin must be
                                     // able to ask "is the database reachable" without first
                                     // impersonating a company that may not even exist yet
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

        var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;
        var companyIdClaim = context.User.FindFirst("CompanyId")?.Value;

        // SysAdmin without impersonation: allow access to code table endpoints
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
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Access denied. No company context found. Contact your administrator."
            });
            return;
        }

        // Delegate to ITenantDbContextFactory — single source of truth for schema resolution.
        // No MasterDb query here — the factory handles it.
        var factory = context.RequestServices.GetRequiredService<ITenantDbContextFactory>();
        var schemaName = await factory.ResolveSchemaAsync(companyId, context.RequestAborted);

        if (schemaName == null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Company is not configured, not provisioned, or inactive. Contact your administrator."
            });
            return;
        }

        // Set Schema on the DI-scoped TenantDbContext so all EF Core queries
        // in this request target the correct tenant schema (e.g., "tenant_1").
        var tenantContext = context.RequestServices.GetRequiredService<TenantDbContext>();
        tenantContext.Schema = schemaName;

        // Lazily ensure migrations are applied (cached — only runs once per schema per process).
        // If migration fails, the exception is now re-thrown by EnsureMigratedAsync so we
        // catch it here and return HTTP 503 instead of proceeding with an unmigrated schema.
        //
        // Without this guard, a failed migration (e.g., Add_ReverseChargeFk_v45 couldn't add
        // VatRegime/ReverseChargeCodeId/InformationalVatAmount columns) would let the request
        // through to InvoiceService, where EF Core generates SQL referencing the missing columns
        // and gets a PostgresException — surfacing as a confusing HTTP 500 with no clear cause.
        try
        {
            await factory.EnsureMigratedAsync(companyId, context.RequestAborted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Tenant schema migration failed for company {CompanyId} (schema '{SchemaName}'). " +
                "Blocking request — schema is not ready for queries.",
                companyId, schemaName);

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                message = "Tenant database is not ready. Schema migration failed. " +
                          "Please retry in a moment or contact your administrator if the problem persists."
            });
            return;
        }

        await _next(context);
    }

    private static bool IsMasterOnlyPath(string path)
    {
        return MasterOnlyPaths.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

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
