namespace Fakvio.Application.Service;

/// <summary>
/// Resolves the current tenant (company) from the request context.
/// Used by middleware and services to determine which tenant database to use.
///
/// The CompanyId comes from one of two sources:
/// 1. Normal users: "CompanyId" claim baked into JWT at login (set in AuthService)
/// 2. SysAdmin impersonation: "CompanyId" claim injected by ImpersonationMiddleware
///    from the X-Company-Id HTTP header
///
/// After ImpersonationMiddleware runs, both paths end up with the same claim name,
/// so this resolver doesn't need to know which source provided it.
/// </summary>
public interface ITenantResolver
{
    /// <summary>
    /// Gets the effective CompanyId for the current request.
    /// Returns the CompanyId claim value (either from JWT or impersonation).
    /// Returns null if no CompanyId is available (SysAdmin without impersonation,
    /// unauthenticated requests, or background jobs).
    /// </summary>
    long? GetCurrentCompanyId();

    /// <summary>
    /// Checks if the current user is a SysAdmin (system administrator).
    /// SysAdmin users don't belong to any company by default — they use
    /// impersonation (X-Company-Id header) to access tenant databases.
    /// </summary>
    bool IsSysAdmin();
}
