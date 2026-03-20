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

using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
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
/// All resolution logic is delegated to ITenantDbContextFactory — this middleware
/// only reads CompanyId from claims and calls the factory. No duplicated MasterDb queries.
///
/// IMPORTANT: Sets Schema on TenantDbContext from BOTH DI scopes because Azure Functions
/// Isolated Worker resolves function classes from InstanceServices, not RequestServices.
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
            await next(context);
            return;
        }

        // Only resolve tenant for authenticated users with a CompanyId claim.
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        var companyIdClaim = httpContext.User.FindFirst("CompanyId")?.Value;
        if (string.IsNullOrEmpty(companyIdClaim) || !long.TryParse(companyIdClaim, out var companyId))
        {
            // No CompanyId — SysAdmin without impersonation, or system endpoints.
            await next(context);
            return;
        }

        // Delegate to ITenantDbContextFactory — single source of truth for schema resolution.
        // The factory does: MasterDb lookup → validation (provisioned? active?) → returns schema name.
        // Resolve factory from Worker scope (where all injected services live).
        var factory = context.InstanceServices.GetRequiredService<ITenantDbContextFactory>();
        var schemaName = await factory.ResolveSchemaAsync(companyId);

        if (schemaName == null)
        {
            _logger.LogWarning("Tenant resolution failed for CompanyId {CompanyId}", companyId);
            await next(context);
            return;
        }

        // ── Set Schema on BOTH DI scopes ──────────────────────────────────────
        // Azure Functions has two separate DI scopes per HTTP request:
        //
        // 1. httpContext.RequestServices — ASP.NET Core scope
        // 2. context.InstanceServices — Functions Worker scope
        //    (where ChatFunctions → ChatController → ChatService → TenantDbContext live)
        //
        // If we only set Schema on one scope, the other scope's TenantDbContext has
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
}
