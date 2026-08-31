// ============================================================================
// DiagnosticFunctions — Health check and manual migration trigger for Azure.
//
// These endpoints help diagnose deployment issues:
// - GET /api/diagnostic/health → auth mode, DB connectivity and migration status [SysAdmin]
// - POST /api/diagnostic/migrate → manually triggers database migrations [SysAdmin]
// - GET /api/diagnostic/auth → dumps the JWT state as the worker sees it [SysAdmin]
//
// All three are SysAdmin-only, because each one leaks or changes something an attacker
// wants: health reports the database auth mode and migration names, migrate mutates the
// schema, and auth echoes the JWT configuration (issuer, audience, secret length) plus
// every claim. The role check is inlined in each function because Azure Functions does
// not run the MVC [Authorize] filter pipeline; AuthorizationLevel.Anonymous on the
// trigger only disables the host key, it is not a public-access switch.
//
// Health is a THIN WRAPPER over Fakvio.API DiagnosticController — same payload in both
// hosts, written once (CLAUDE.md, "API + Functions duplication"). It follows the shape
// the generator emits for every other controller, including the inlined [Authorize]
// check.
//
// Migrate and Auth have no controller counterpart and stay hand-written here.
// WARNING: re-running Fakvio.Functions.Generator would emit its own DiagnosticFunctions
// for DiagnosticController and overwrite this file — keep Migrate/Auth if that happens.
// ============================================================================

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Functions.Generated;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Fakvio.Functions.HttpFunctions;

/// <summary>
/// Diagnostic endpoints for Azure deployment troubleshooting.
/// Call these manually to check DB status and trigger migrations.
/// </summary>
public class DiagnosticFunctions
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly DiagnosticController _controller;
    private readonly ILogger<DiagnosticFunctions> _logger;

    public DiagnosticFunctions(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        DiagnosticController controller,
        ILogger<DiagnosticFunctions> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _controller = controller;
        _logger = logger;
    }

    /// <summary>
    /// Health check — reports the resolved database auth mode plus master DB connectivity
    /// and migration status. Thin wrapper: the payload is built by DiagnosticController so
    /// both hosts answer identically.
    /// Call: GET https://your-function-app.azurewebsites.net/api/diagnostic/health
    /// </summary>
    [Function("Diagnostic_Health")]
    public async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/diagnostic/health")] HttpRequest req)
    {
        // Wire up the controller's HttpContext so it can access User claims, Request, etc.
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        // Authorization check: [Authorize(Roles = "SysAdmin")] on DiagnosticController.
        // AuthorizationLevel.Anonymous above only disables the Functions host key — the JWT
        // check has to be inlined here because the MVC filter pipeline does not run.
        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        // Role check: user must be in one of [SysAdmin]
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var ct = req.HttpContext.RequestAborted;

        // Call the controller action and normalize the response
        return FunctionResultHelper.Normalize(await _controller.Health(ct));
    }

    /// <summary>
    /// Manually triggers database migrations. SysAdmin only — it writes to the schema.
    /// Call: POST https://your-function-app.azurewebsites.net/api/diagnostic/migrate
    /// </summary>
    [Function("Diagnostic_Migrate")]
    public async Task<IActionResult> Migrate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/diagnostic/migrate")] HttpRequest req)
    {
        // Authorization check: [Authorize(Roles = "SysAdmin")]
        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        // Role check: user must be in one of [SysAdmin]
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var result = new Dictionary<string, object>();

        try
        {
            using var scope = _serviceProvider.CreateScope();

            // Step 1: Migrate Master DB
            _logger.LogInformation("Manual migration: starting master DB...");
            var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

            var pendingBefore = (await masterDb.Database.GetPendingMigrationsAsync()).ToList();
            result["masterPendingBefore"] = pendingBefore.Count;
            result["masterPendingMigrationNames"] = pendingBefore;

            await masterDb.Database.MigrateAsync();
            _logger.LogInformation("Manual migration: master DB migrated successfully");

            var appliedAfter = (await masterDb.Database.GetAppliedMigrationsAsync()).ToList();
            result["masterAppliedAfter"] = appliedAfter.Count;
            result["masterMigrateSuccess"] = true;

            // Step 2: Migrate Tenant DBs
            try
            {
                var provisioningService = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
                var migrated = await provisioningService.MigrateAllTenantsAsync();
                result["tenantsMigrated"] = migrated;
                _logger.LogInformation("Manual migration: migrated {Count} tenant DB(s)", migrated);
            }
            catch (Exception ex)
            {
                result["tenantMigrationError"] = ex.Message;
                _logger.LogError(ex, "Manual migration: tenant migration failed");
            }
        }
        catch (Exception ex)
        {
            result["masterMigrateSuccess"] = false;
            result["masterMigrateError"] = ex.Message;
            result["masterMigrateStackTrace"] = ex.StackTrace;
            _logger.LogError(ex, "Manual migration: master DB migration failed");
        }

        result["timestamp"] = DateTime.UtcNow;
        return new OkObjectResult(result);
    }

    /// <summary>
    /// Auth diagnostic — dumps the full authentication state as seen by the function.
    /// This helps debug JWT middleware issues: shows Authorization header presence,
    /// whether HttpContext.User is authenticated, all claims, and manual JWT validation.
    /// SysAdmin only — the payload contains the JWT configuration and every claim,
    /// so an anonymous caller gets 401 and no diagnostic data at all.
    /// Call: GET https://your-function-app.azurewebsites.net/api/diagnostic/auth
    /// Pass a SysAdmin Bearer token in the Authorization header.
    /// </summary>
    [Function("Diagnostic_Auth")]
    public IActionResult AuthDiagnostic(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/diagnostic/auth")] HttpRequest req)
    {
        // Authorization check: [Authorize(Roles = "SysAdmin")]
        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        // Role check: user must be in one of [SysAdmin]
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var result = new Dictionary<string, object>();

        // 1. Check what the function sees on HttpContext.User (set by JWT middleware)
        var user = req.HttpContext.User;
        result["userIdentityIsAuthenticated"] = user.Identity?.IsAuthenticated ?? false;
        result["userIdentityAuthenticationType"] = user.Identity?.AuthenticationType ?? "(null)";
        result["userIdentityName"] = user.Identity?.Name ?? "(null)";

        // 2. Dump all claims (shows what the middleware set)
        var claims = user.Claims.Select(c => new { c.Type, c.Value }).ToList();
        result["claimsCount"] = claims.Count;
        result["claims"] = claims;

        // 3. Check IsInRole for common roles
        result["isInRole_SysAdmin"] = user.IsInRole("SysAdmin");
        result["isInRole_Admin"] = user.IsInRole("Admin");
        result["isInRole_User"] = user.IsInRole("User");

        // 4. Show the raw Authorization header (mask the token for security)
        var authHeader = req.Headers["Authorization"].FirstOrDefault();
        result["authorizationHeaderPresent"] = !string.IsNullOrEmpty(authHeader);
        if (!string.IsNullOrEmpty(authHeader))
        {
            // Show first 20 + last 10 chars to confirm token is being sent
            var masked = authHeader.Length > 40
                ? authHeader[..20] + "..." + authHeader[^10..]
                : authHeader;
            result["authorizationHeaderMasked"] = masked;
            result["authorizationHeaderLength"] = authHeader.Length;
        }

        // 5. Check JWT configuration
        var jwtSecret = _configuration["JwtSettings:Secret"];
        var jwtIssuer = _configuration["JwtSettings:Issuer"] ?? "Fakvio";
        var jwtAudience = _configuration["JwtSettings:Audience"] ?? "FakvioClient";
        result["jwtSecretConfigured"] = !string.IsNullOrEmpty(jwtSecret);
        result["jwtSecretLength"] = jwtSecret?.Length ?? 0;
        result["jwtIssuer"] = jwtIssuer;
        result["jwtAudience"] = jwtAudience;

        // 6. If a Bearer token is present, try to validate it manually right here
        //    (bypasses middleware entirely — pure standalone validation test)
        if (!string.IsNullOrEmpty(authHeader) &&
            authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(jwtSecret))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            result["tokenExtracted"] = true;
            result["tokenLength"] = token.Length;

            try
            {
                // Decode token WITHOUT validation to inspect claims
                var handler = new JwtSecurityTokenHandler();
                if (handler.CanReadToken(token))
                {
                    var jwtToken = handler.ReadJwtToken(token);
                    result["tokenIssuer"] = jwtToken.Issuer;
                    result["tokenAudience"] = string.Join(", ", jwtToken.Audiences);
                    result["tokenValidFrom"] = jwtToken.ValidFrom;
                    result["tokenValidTo"] = jwtToken.ValidTo;
                    result["tokenIsExpired"] = jwtToken.ValidTo < DateTime.UtcNow;
                    result["tokenRawClaims"] = jwtToken.Claims
                        .Select(c => new { c.Type, c.Value }).ToList();
                }
                else
                {
                    result["tokenCanRead"] = false;
                }

                // Now try full validation
                var validationParams = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtIssuer,
                    ValidAudience = jwtAudience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwtSecret)),
                    ClockSkew = TimeSpan.FromMinutes(2),
                    RoleClaimType = ClaimTypes.Role
                };

                var validationHandler = new JwtSecurityTokenHandler();
                validationHandler.MapInboundClaims = true;
                var principal = validationHandler.ValidateToken(
                    token, validationParams, out _);

                result["manualValidation"] = "SUCCESS";
                result["manualValidationIsAuthenticated"] =
                    principal.Identity?.IsAuthenticated ?? false;
                result["manualValidationClaims"] = principal.Claims
                    .Select(c => new { c.Type, c.Value }).ToList();
            }
            catch (SecurityTokenExpiredException ex)
            {
                result["manualValidation"] = "EXPIRED";
                result["manualValidationError"] = ex.Message;
            }
            catch (SecurityTokenException ex)
            {
                result["manualValidation"] = "FAILED";
                result["manualValidationError"] = ex.Message;
            }
            catch (Exception ex)
            {
                result["manualValidation"] = "ERROR";
                result["manualValidationError"] = ex.Message;
            }
        }

        result["timestamp"] = DateTime.UtcNow;
        _logger.LogInformation("Auth diagnostic called — authenticated={IsAuth}, claims={ClaimCount}",
            user.Identity?.IsAuthenticated, claims.Count);

        return new OkObjectResult(result);
    }

}
