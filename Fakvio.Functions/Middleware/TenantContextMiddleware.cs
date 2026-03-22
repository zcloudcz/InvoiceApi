// ============================================================================
// TenantContextMiddleware — Sets the tenant schema on TenantDbContext per request.
//
// Delegates ALL tenant resolution logic to ITenantDbContextFactory.ResolveSchemaAsync
// (single source of truth — no MasterDb lookup duplication here).
//
// CRITICAL: Azure Functions Isolated Worker has TWO DI scopes per request:
//   - httpContext.RequestServices → ASP.NET Core scope
//   - context.InstanceServices   → Functions Worker scope (where function classes live)
// We MUST set Schema on BOTH scopes. See comments below for details.
// ============================================================================

using System.Security.Claims;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Middleware;

/// <summary>
/// Azure Functions middleware that resolves the tenant schema and sets it on TenantDbContext.
///
/// Must run AFTER JwtAuthenticationMiddleware (needs User claims populated).
///
/// Mirrors the API project's TenantContextMiddleware behavior:
/// - Master-only paths (auth, user, company, etc.) → skip tenant check
/// - SysAdmin without impersonation + code table path → allow (dual-context)
/// - SysAdmin without impersonation + tenant endpoint → 403 (must impersonate)
/// - No CompanyId on tenant endpoint → 403
/// - Invalid/inactive/unprovisioned tenant → 403
/// - Valid tenant → set Schema on BOTH DI scopes + lazy migration
///
/// Junior note: Without this validation, SysAdmin could hit /api/chat/conversations
/// without impersonation → no Schema set → EF queries "public" schema → crash.
/// </summary>
public class TenantContextMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<TenantContextMiddleware> _logger;

    /// <summary>
    /// Route prefixes that are master-only — no tenant context needed.
    /// Must match the API middleware's MasterOnlyPaths.
    /// </summary>
    private static readonly string[] MasterOnlyPrefixes =
    [
        "/api/auth",
        "/api/user",
        "/api/company",
        "/api/system-configuration",
        "/api/dashboard/sysadmin",
        "/api/logs",
        "/api/twofactor",
        "/api/cloud-storage",
        "/api/email"                // SysAdmin email — uses system SMTP, no tenant needed
    ];

    /// <summary>
    /// Route prefixes for code table endpoints that SysAdmin can access WITHOUT impersonation.
    /// These services use dual-context (MasterDbContext fallback when no tenant).
    /// </summary>
    private static readonly string[] SysAdminCodeTablePrefixes =
    [
        "/api/currency",
        "/api/vatrate",
        "/api/contenttemplate",
        "/api/numbersequence/formats"
    ];

    public TenantContextMiddleware(ILogger<TenantContextMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();

        // No HTTP context (timer triggers, etc.) — skip tenant resolution.
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        var path = httpContext.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Skip tenant validation for master-only endpoints and non-API paths.
        if (!path.StartsWith("/api/") || IsMasterOnlyPath(path))
        {
            await next(context);
            return;
        }

        // Skip for unauthenticated requests — let [Authorize] / auth checks handle it.
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var roleClaim = httpContext.User.FindFirst(ClaimTypes.Role)?.Value;
        var companyIdClaim = httpContext.User.FindFirst("CompanyId")?.Value;

        // ── SysAdmin without impersonation ────────────────────────────────────
        // SysAdmin must use X-Company-Id header to specify which tenant to access.
        // Without it, only code table endpoints (dual-context) are allowed.
        if (roleClaim == "SysAdmin" && string.IsNullOrEmpty(companyIdClaim))
        {
            if (IsSysAdminCodeTablePath(path))
            {
                _logger.LogDebug("SysAdmin accessing code table {Path} without impersonation", path);
                await next(context);
                return;
            }

            // SysAdmin hitting a tenant endpoint (e.g., /api/chat, /api/invoice)
            // without impersonation → 403.
            _logger.LogWarning(
                "SysAdmin tried to access tenant endpoint {Path} without impersonation", path);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                message = "SysAdmin must impersonate a company to access tenant endpoints. " +
                          "Add the X-Company-Id header with the target company ID."
            });
            return;
        }

        // ── Regular user or impersonating SysAdmin — must have valid CompanyId ──
        if (string.IsNullOrEmpty(companyIdClaim) || !long.TryParse(companyIdClaim, out var companyId))
        {
            _logger.LogWarning("No CompanyId claim for tenant endpoint {Path}", path);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                message = "Access denied. No company context found. Contact your administrator."
            });
            return;
        }

        // Delegate to ITenantDbContextFactory — single source of truth for schema resolution.
        // Resolve factory from Worker scope (where all injected services live).
        var factory = context.InstanceServices.GetRequiredService<ITenantDbContextFactory>();
        var schemaName = await factory.ResolveSchemaAsync(companyId);

        if (schemaName == null)
        {
            _logger.LogWarning("Tenant resolution failed for CompanyId {CompanyId} on {Path}",
                companyId, path);
            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                message = "Company is not configured, not provisioned, or inactive. Contact your administrator."
            });
            return;
        }

        // ── Set Schema on BOTH DI scopes ──────────────────────────────────────
        // Azure Functions has two separate DI scopes per HTTP request:
        // 1. httpContext.RequestServices — ASP.NET Core scope
        // 2. context.InstanceServices — Functions Worker scope
        //    (where ChatFunctions → ChatController → ChatService → TenantDbContext live)
        //
        // If we only set Schema on one scope, the other's TenantDbContext has
        // Schema = null → EF generates SQL without schema prefix → "relation does not exist".
        var tenantContextFromHttp = httpContext.RequestServices.GetRequiredService<TenantDbContext>();
        tenantContextFromHttp.Schema = schemaName;

        var tenantContextFromWorker = context.InstanceServices.GetRequiredService<TenantDbContext>();
        tenantContextFromWorker.Schema = schemaName;

        // Lazily ensure migrations are applied (cached — only runs once per schema per process).
        await factory.EnsureMigratedAsync(companyId);

        _logger.LogDebug("Tenant resolved: CompanyId {CompanyId} → schema '{Schema}'",
            companyId, schemaName);

        await next(context);
    }

    private static bool IsMasterOnlyPath(string path)
    {
        return MasterOnlyPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSysAdminCodeTablePath(string path)
    {
        return SysAdminCodeTablePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
