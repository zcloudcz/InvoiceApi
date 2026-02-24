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
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace InvoiceApi.Functions.Middleware;

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
            await next(context);
            return;
        }

        // Extract the Bearer token from the Authorization header
        var authHeader = httpContext.Request.Headers["Authorization"].FirstOrDefault();
        if (!string.IsNullOrEmpty(authHeader) && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();

            try
            {
                // Build the same TokenValidationParameters as AuthenticationExtensions.cs
                var jwtSecret = _configuration["JwtSettings:Secret"];
                var jwtIssuer = _configuration["JwtSettings:Issuer"] ?? "InvoiceApi";
                var jwtAudience = _configuration["JwtSettings:Audience"] ?? "InvoiceApiClient";

                if (!string.IsNullOrEmpty(jwtSecret))
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
                        ClockSkew = TimeSpan.Zero
                    };

                    var tokenHandler = new JwtSecurityTokenHandler();
                    var principal = tokenHandler.ValidateToken(token, validationParameters, out _);

                    // Set the authenticated user on HttpContext so all downstream code
                    // (controller actions, auth checks in generated functions) sees it.
                    httpContext.User = principal;
                }
            }
            catch (SecurityTokenException ex)
            {
                // Token validation failed (expired, invalid signature, etc.)
                // Leave HttpContext.User as anonymous — the function's auth check
                // will return 401 as expected.
                _logger.LogDebug(ex, "JWT validation failed: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected error during JWT validation");
            }
        }

        await next(context);
    }
}
