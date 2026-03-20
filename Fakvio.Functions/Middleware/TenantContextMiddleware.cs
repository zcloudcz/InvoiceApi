// ============================================================================
// TenantContextMiddleware — Sets the tenant schema on TenantDbContext per request.
//
// In the API project, this is handled by Fakvio.API.Middleware.TenantContextMiddleware
// which runs as ASP.NET Core middleware. Azure Functions don't have that pipeline,
// so this middleware does the same thing in the Functions Worker pipeline.
//
// Flow:
// 1. Read CompanyId from the authenticated user's claims (set by JwtAuthenticationMiddleware)
// 2. Look up CompanySystemSettings in the master DB to get the schema name
// 3. Set TenantDbContext.Schema so all EF Core queries target the correct tenant schema
//
// CRITICAL: Azure Functions Isolated Worker has TWO DI scopes per request:
//   - httpContext.RequestServices → ASP.NET Core scope (used by ASP.NET middleware)
//   - context.InstanceServices   → Functions Worker scope (used by function classes + injected services)
// These are DIFFERENT instances! The function class (ChatFunctions) and its dependencies
// (ChatController → ChatService → TenantDbContext) are resolved from context.InstanceServices.
// We MUST set Schema on the TenantDbContext from BOTH scopes to ensure it works regardless
// of which scope resolves the DbContext.
//
// Without this, TenantDbContext has no schema set → queries go to "public" schema
// → "relation does not exist" errors for tenant-only tables (ChatConversation, etc.)
// ============================================================================

using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Middleware;

/// <summary>
/// Azure Functions middleware that resolves the tenant schema from the current user's
/// CompanyId claim and sets it on the scoped TenantDbContext.
///
/// Must run AFTER JwtAuthenticationMiddleware (so User claims are populated).
///
/// IMPORTANT: Sets Schema on TenantDbContext from BOTH DI scopes (httpContext.RequestServices
/// AND context.InstanceServices). In Azure Functions Isolated Worker, these are separate scopes —
/// the function class and its injected services come from InstanceServices, NOT RequestServices.
/// If we only set Schema on one scope, the other scope's TenantDbContext has Schema = null,
/// causing EF Core to generate queries without a schema prefix → "relation does not exist".
///
/// Junior note: This is the Functions equivalent of Fakvio.API.Middleware.TenantContextMiddleware.
/// The API version only needs one scope (HttpContext.RequestServices) because ASP.NET Core MVC
/// resolves controllers from the same scope. Functions has two scopes — that's the key difference.
/// </summary>
public class TenantContextMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<TenantContextMiddleware> _logger;

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
            _logger.LogDebug("TenantMiddleware: No HttpContext — skipping (timer trigger?)");
            await next(context);
            return;
        }

        var path = httpContext.Request.Path.Value ?? "";
        var isAuth = httpContext.User.Identity?.IsAuthenticated == true;

        _logger.LogInformation(
            "TenantMiddleware: {Method} {Path} — IsAuthenticated={IsAuth}, Claims=[{Claims}]",
            httpContext.Request.Method, path, isAuth,
            string.Join(", ", httpContext.User.Claims.Select(c => $"{c.Type}={c.Value}")));

        // Only resolve tenant for authenticated users with a CompanyId claim.
        if (!isAuth)
        {
            _logger.LogDebug("TenantMiddleware: Not authenticated — skipping for {Path}", path);
            await next(context);
            return;
        }

        var companyIdClaim = httpContext.User.FindFirst("CompanyId")?.Value;
        if (string.IsNullOrEmpty(companyIdClaim) || !long.TryParse(companyIdClaim, out var companyId))
        {
            // No CompanyId — SysAdmin without impersonation, or system endpoints.
            _logger.LogWarning("TenantMiddleware: No CompanyId claim for {Path} (CompanyId='{Claim}')",
                path, companyIdClaim);
            await next(context);
            return;
        }

        // Look up the tenant's schema name from CompanySystemSettings in the master DB.
        // Use the HttpContext scope for the master DB lookup (MasterDbContext is the same in both scopes).
        var masterContext = httpContext.RequestServices.GetRequiredService<MasterDbContext>();
        var tenantSettings = await masterContext.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId);

        if (tenantSettings == null || !tenantSettings.IsProvisioned || !tenantSettings.IsActive)
        {
            _logger.LogWarning(
                "Tenant resolution failed for CompanyId {CompanyId}: settings={Exists}, provisioned={Provisioned}, active={Active}",
                companyId,
                tenantSettings != null,
                tenantSettings?.IsProvisioned,
                tenantSettings?.IsActive);
            await next(context);
            return;
        }

        // ── Set Schema on BOTH DI scopes ──────────────────────────────────────
        // Azure Functions Isolated Worker has two separate DI scopes per request:
        //
        // 1. httpContext.RequestServices — the ASP.NET Core scope.
        //    Used by ASP.NET Core middleware and anything that resolves via HttpContext.
        //
        // 2. context.InstanceServices — the Functions Worker scope.
        //    This is where the function class (e.g., ChatFunctions) and all its
        //    constructor-injected dependencies live (ChatController → ChatService → TenantDbContext).
        //
        // If we only set Schema on httpContext.RequestServices, the TenantDbContext used by
        // ChatService (from InstanceServices) still has Schema = null → EF generates SQL
        // without schema prefix → "relation does not exist" error.
        //
        // We set Schema on BOTH to cover all resolution paths.

        // Scope 1: ASP.NET Core scope (httpContext.RequestServices)
        var tenantContextFromHttp = httpContext.RequestServices.GetRequiredService<TenantDbContext>();
        tenantContextFromHttp.Schema = tenantSettings.SchemaName;

        // Scope 2: Functions Worker scope (context.InstanceServices) — where injected services live
        var tenantContextFromWorker = context.InstanceServices.GetRequiredService<TenantDbContext>();
        tenantContextFromWorker.Schema = tenantSettings.SchemaName;

        _logger.LogDebug(
            "Tenant resolved: CompanyId {CompanyId} → schema '{Schema}' " +
            "(set on both HttpContext and Worker scopes, same instance: {SameInstance})",
            companyId, tenantSettings.SchemaName,
            ReferenceEquals(tenantContextFromHttp, tenantContextFromWorker));

        await next(context);
    }
}
