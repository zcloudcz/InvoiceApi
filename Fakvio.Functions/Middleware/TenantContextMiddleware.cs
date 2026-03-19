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
/// Junior note: This is the Functions equivalent of Fakvio.API.Middleware.TenantContextMiddleware.
/// Both do the same thing — read CompanyId → look up schema name → set TenantDbContext.Schema.
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

        // Look up the tenant's schema name from CompanySystemSettings in the master DB.
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

        // Set the schema on the DI-resolved TenantDbContext for this request scope.
        // This ensures all EF Core queries target the correct tenant schema (e.g., "tenant_1").
        var tenantContext = httpContext.RequestServices.GetRequiredService<TenantDbContext>();
        tenantContext.Schema = tenantSettings.SchemaName;

        _logger.LogDebug("Tenant resolved: CompanyId {CompanyId} → schema '{Schema}'",
            companyId, tenantSettings.SchemaName);

        await next(context);
    }
}
