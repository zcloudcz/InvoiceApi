// ============================================================================
// JwtAuthenticationMiddleware — Validates JWT Bearer tokens in Azure Functions.
//
// Azure Functions Isolated Worker does NOT have UseAuthentication()/UseAuthorization()
// like ASP.NET Core. Instead, we manually validate the JWT token from the
// Authorization header and set HttpContext.User to the authenticated ClaimsPrincipal.
//
// This middleware runs early in the pipeline (after CORS) so that all subsequent
// function code sees the correct User identity and claims.
//
// Without this middleware, HttpContext.User is always anonymous, and every
// [Authorize] check in the generated function wrappers returns 401.
// ============================================================================

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Fakvio.Functions.Middleware;

/// <summary>
/// Custom middleware that validates JWT Bearer tokens for Azure Functions.
/// Reads the "Authorization: Bearer {token}" header, validates the token using
/// the same JwtSettings configuration as the API project, and populates
/// HttpContext.User with the authenticated ClaimsPrincipal (including all claims
/// like UserId, CompanyId, Role, etc.).
/// </summary>
public class JwtAuthenticationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<JwtAuthenticationMiddleware> _logger;

    public JwtAuthenticationMiddleware(
        IConfiguration configuration,
        ILogger<JwtAuthenticationMiddleware> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        // Only process HTTP triggers — timer triggers don't have HttpContext
        var httpContext = context.GetHttpContext();
        if (httpContext == null)
        {
            _logger.LogInformation("JWT middleware: GetHttpContext() returned null (non-HTTP trigger)");
            await next(context);
            return;
        }

        // Azure Functions Isolated Worker has two DI scopes: the ASP.NET Core HTTP
        // scope (httpContext.RequestServices) and the Worker scope (context.InstanceServices).
        // IHttpContextAccessor in the Worker scope does NOT automatically see the HTTP
        // pipeline's HttpContext. Wire it up here so that all downstream services
        // (CurrentUserService, FileAttachmentService, etc.) can read User claims
        // without each Function wrapper needing to do it manually.
        var httpContextAccessor = context.InstanceServices.GetService<IHttpContextAccessor>();
        if (httpContextAccessor != null)
            httpContextAccessor.HttpContext = httpContext;

        var path = httpContext.Request.Path.Value ?? "(unknown)";
        var method = httpContext.Request.Method;

        // Extract the Bearer token from the Authorization header
        var authHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            _logger.LogInformation("JWT middleware: Bearer token found for {Method} {Path} (token length={Length})",
                method, path, token.Length);

            try
            {
                // Build the same TokenValidationParameters as AuthenticationExtensions.cs
                var jwtSecret = _configuration["JwtSettings:Secret"];
                var jwtIssuer = _configuration["JwtSettings:Issuer"] ?? "Fakvio";
                var jwtAudience = _configuration["JwtSettings:Audience"] ?? "FakvioClient";

                if (string.IsNullOrEmpty(jwtSecret))
                {
                    // CRITICAL: JwtSettings:Secret is not configured — ALL auth will fail.
                    // Check Azure App Settings: JwtSettings__Secret must be set.
                    _logger.LogError("JWT secret is NOT configured — set JwtSettings__Secret in Azure App Settings");
                }
                else
                {
                    var validationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        ValidIssuer = jwtIssuer,
                        ValidAudience = jwtAudience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                        // Allow 2 minutes of clock skew to handle minor Azure clock differences
                        ClockSkew = TimeSpan.FromMinutes(2),
                        // Ensure role claims are properly mapped so IsInRole() works.
                        // JWT "role" claim must map to ClaimTypes.Role for authorization checks.
                        RoleClaimType = ClaimTypes.Role
                    };

                    var tokenHandler = new JwtSecurityTokenHandler();
                    // Ensure inbound claims are mapped to standard .NET claim types
                    // (e.g., JWT "role" → ClaimTypes.Role). Required for IsInRole() to work.
                    tokenHandler.MapInboundClaims = true;
                    var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

                    // Set the authenticated user on HttpContext so all downstream code
                    // (controller actions, auth checks in generated functions) sees it.
                    httpContext.User = principal;

                    _logger.LogInformation(
                        "JWT middleware: VALIDATED for {Method} {Path} — user={UserId}, role={Role}, isAuth={IsAuth}",
                        method, path,
                        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "(no NameIdentifier)",
                        principal.FindFirst(ClaimTypes.Role)?.Value ?? "(no Role)",
                        principal.Identity?.IsAuthenticated);
                }
            }
            catch (SecurityTokenExpiredException ex)
            {
                // Token expired — user needs to log in again
                _logger.LogWarning("JWT middleware: Token EXPIRED for {Method} {Path}: {Message}",
                    method, path, ex.Message);
            }
            catch (SecurityTokenException ex)
            {
                // Token validation failed (invalid signature, wrong issuer, etc.)
                _logger.LogWarning("JWT middleware: Validation FAILED for {Method} {Path}: {Message}",
                    method, path, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JWT middleware: Unexpected error for {Method} {Path}", method, path);
            }
        }
        else
        {
            // No Authorization header — anonymous request.
            // This is normal for unauthenticated endpoints (health, login, CORS preflight).
            _logger.LogInformation("JWT middleware: No Bearer token for {Method} {Path}",
                method, path);
        }

        // Verify: log what the HttpContext.User looks like AFTER our processing
        _logger.LogInformation("JWT middleware: AFTER processing {Method} {Path} — User.IsAuthenticated={IsAuth}",
            method, path, httpContext.User.Identity?.IsAuthenticated);

        await next(context);
    }
}
