using System.Security.Claims;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace Fakvio.Functions.Middleware;

/// <summary>
/// Azure Functions version of Fakvio.API.Middleware.ImpersonationMiddleware.
///
/// When a SysAdmin sends the "X-Company-Id" header, this middleware adds
/// a "CompanyId" claim to the request's identity. This allows SysAdmin to
/// "act as" a specific company — the existing CompanyId-based tenant resolution
/// in TenantContextMiddleware picks it up automatically.
///
/// Must run AFTER JwtAuthenticationMiddleware (needs User claims populated)
/// and BEFORE TenantContextMiddleware (which reads the CompanyId claim).
///
/// Security: Only SysAdmin role is allowed to impersonate. For other roles,
/// the header is ignored (they already have a CompanyId from their JWT).
///
/// Junior note: This is the Functions equivalent of Fakvio.API.Middleware.ImpersonationMiddleware.
/// Both do the same thing — read X-Company-Id header → add CompanyId claim.
/// The API version uses ASP.NET Core middleware (RequestDelegate), this one uses
/// IFunctionsWorkerMiddleware (FunctionContext + FunctionExecutionDelegate).
/// </summary>
public class ImpersonationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<ImpersonationMiddleware> _logger;

    public ImpersonationMiddleware(ILogger<ImpersonationMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var httpContext = context.GetHttpContext();

        // No HTTP context (timer triggers, etc.) — skip.
        if (httpContext == null)
        {
            await next(context);
            return;
        }

        // Only process if the user is authenticated and is a SysAdmin.
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            var roleClaim = httpContext.User.FindFirst(ClaimTypes.Role)?.Value;

            if (roleClaim == "SysAdmin" &&
                httpContext.Request.Headers.TryGetValue("X-Company-Id", out var companyIdHeader))
            {
                var headerValue = companyIdHeader.FirstOrDefault();

                if (!string.IsNullOrEmpty(headerValue) && long.TryParse(headerValue, out var companyId))
                {
                    _logger.LogDebug("SysAdmin impersonating company {CompanyId}", companyId);

                    // Add CompanyId claim to the current identity so that
                    // TenantContextMiddleware reads it and sets the correct schema.
                    var identity = httpContext.User.Identity as ClaimsIdentity;
                    if (identity != null)
                    {
                        // Remove any existing CompanyId claim (SysAdmin normally has none).
                        var existingClaim = identity.FindFirst("CompanyId");
                        if (existingClaim != null)
                        {
                            identity.RemoveClaim(existingClaim);
                        }

                        // Add the impersonated company ID as a claim.
                        identity.AddClaim(new Claim("CompanyId", companyId.ToString()));
                    }
                }
            }
        }

        await next(context);
    }
}
