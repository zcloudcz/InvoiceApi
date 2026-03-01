using System.Security.Claims;

namespace Fakvio.API.Middleware;

/// <summary>
/// Middleware that handles SysAdmin company impersonation.
/// When a SysAdmin sends the "X-Company-Id" header, this middleware adds
/// a "CompanyId" claim to the request's identity, so controllers can use
/// GetCurrentUserCompanyId() to get the impersonated company automatically.
///
/// This allows SysAdmin to "act as" a specific company without modifying
/// every controller — the existing CompanyId-based filtering just works.
///
/// Security: Only SysAdmin role is allowed to impersonate. For other roles,
/// the header is ignored (they already have a CompanyId from their JWT).
/// </summary>
public class ImpersonationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ImpersonationMiddleware> _logger;

    public ImpersonationMiddleware(RequestDelegate next, ILogger<ImpersonationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only process if the user is authenticated and is a SysAdmin
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var roleClaim = context.User.FindFirst(ClaimTypes.Role)?.Value;

            if (roleClaim == "SysAdmin" &&
                context.Request.Headers.TryGetValue("X-Company-Id", out var companyIdHeader))
            {
                var headerValue = companyIdHeader.FirstOrDefault();

                if (!string.IsNullOrEmpty(headerValue) && long.TryParse(headerValue, out var companyId))
                {
                    _logger.LogDebug(
                        "SysAdmin impersonating company {CompanyId}", companyId);

                    // Add CompanyId claim to the current identity so that
                    // GetCurrentUserCompanyId() in controllers returns this value
                    var identity = context.User.Identity as ClaimsIdentity;
                    if (identity != null)
                    {
                        // Remove any existing CompanyId claim (SysAdmin normally has none)
                        var existingClaim = identity.FindFirst("CompanyId");
                        if (existingClaim != null)
                        {
                            identity.RemoveClaim(existingClaim);
                        }

                        // Add the impersonated company ID as a claim
                        identity.AddClaim(new Claim("CompanyId", companyId.ToString()));
                    }
                }
            }
        }

        await _next(context);
    }
}

/// <summary>
/// Extension method to register the impersonation middleware in the pipeline.
/// Must be called AFTER UseAuthentication() and UseAuthorization().
/// </summary>
public static class ImpersonationMiddlewareExtensions
{
    public static IApplicationBuilder UseImpersonation(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ImpersonationMiddleware>();
    }
}
