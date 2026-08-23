// ============================================================================
// DiagnosticFunctions — Health check and manual migration trigger for Azure.
//
// These endpoints help diagnose deployment issues:
// - GET /api/diagnostic/health → checks DB connectivity and migration status
// - POST /api/diagnostic/migrate → manually triggers database migrations
//
// - GET /api/diagnostic/auth → dumps the JWT state as the worker sees it
//
// Health is anonymous so Azure probes can call it. Migrate and Auth are
// SysAdmin-only: migrate mutates the database, and auth echoes the JWT
// configuration (issuer, audience, secret length) plus every claim — both
// are attacker gold if left open. The role check is inlined in each function
// because Azure Functions does not run the MVC [Authorize] filter pipeline;
// AuthorizationLevel.Anonymous on the trigger only disables the host key.
// ============================================================================

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Data;
// PostgreSQL: Using Npgsql instead of Microsoft.Data.SqlClient for connection string parsing
using Npgsql;
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
    private readonly ILogger<DiagnosticFunctions> _logger;

    public DiagnosticFunctions(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<DiagnosticFunctions> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Health check — tests database connectivity and reports migration status.
    /// Call: GET https://your-function-app.azurewebsites.net/api/diagnostic/health
    /// </summary>
    [Function("Diagnostic_Health")]
    public async Task<IActionResult> Health(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/diagnostic/health")] HttpRequest req)
    {
        var result = new Dictionary<string, object>();

        // Check connection strings (masked)
        // PostgreSQL: Changed from "MasterConnection" to "DefaultConnection" for PostgreSQL migration
        var masterConn = _configuration.GetConnectionString("DefaultConnection");
        var tenantConn = _configuration.GetConnectionString("TenantTemplateConnection");
        result["masterConnectionConfigured"] = !string.IsNullOrEmpty(masterConn);
        result["tenantTemplateConnectionConfigured"] = !string.IsNullOrEmpty(tenantConn);

        if (!string.IsNullOrEmpty(masterConn))
        {
            result["masterConnectionServer"] = MaskConnectionString(masterConn);
        }

        // Test Master DB connection
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var masterDb = scope.ServiceProvider.GetRequiredService<MasterDbContext>();

            // Test raw connection
            var canConnect = await masterDb.Database.CanConnectAsync();
            result["masterDbCanConnect"] = canConnect;

            if (canConnect)
            {
                // Check pending migrations
                var pending = await masterDb.Database.GetPendingMigrationsAsync();
                var applied = await masterDb.Database.GetAppliedMigrationsAsync();
                result["masterDbAppliedMigrations"] = applied.Count();
                result["masterDbPendingMigrations"] = pending.Count();
                result["masterDbPendingMigrationNames"] = pending.ToList();

                // Check if key tables exist
                try
                {
                    var userCount = await masterDb.User.CountAsync();
                    result["masterDbUserCount"] = userCount;
                }
                catch (Exception ex)
                {
                    result["masterDbUserTableError"] = ex.Message;
                }
            }
        }
        catch (Exception ex)
        {
            result["masterDbError"] = ex.Message;
            _logger.LogError(ex, "Health check: Master DB connection failed");
        }

        // Top-level flag: true only when we can actually connect to the database
        // and there are no pending migrations. This is the single field to check
        // in monitoring dashboards or Azure health probes.
        var isConnected = result.TryGetValue("masterDbCanConnect", out var canConn) && canConn is true;
        var hasPending = result.TryGetValue("masterDbPendingMigrations", out var pendingCount) && pendingCount is int p && p > 0;
        result["databaseConnected"] = isConnected;
        result["databaseReady"] = isConnected && !hasPending;

        result["timestamp"] = DateTime.UtcNow;
        result["environment"] = Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") ?? "unknown";

        // Return 503 Service Unavailable when database is not reachable,
        // so Azure health probes and monitoring tools can detect the issue.
        if (!isConnected)
        {
            return new ObjectResult(result) { StatusCode = 503 };
        }

        return new OkObjectResult(result);
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

    /// <summary>
    /// Masks sensitive parts of a connection string for safe display.
    /// Shows server/host and database name, hides password.
    /// PostgreSQL: Uses NpgsqlConnectionStringBuilder instead of SqlConnectionStringBuilder.
    /// Property mapping: DataSource → Host, InitialCatalog → Database, UserID → Username.
    /// </summary>
    private static string MaskConnectionString(string connectionString)
    {
        try
        {
            // PostgreSQL: NpgsqlConnectionStringBuilder parses PostgreSQL connection strings.
            // Property names differ from SQL Server:
            //   SQL Server DataSource    → PostgreSQL Host
            //   SQL Server InitialCatalog → PostgreSQL Database
            //   SQL Server UserID        → PostgreSQL Username
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"Host={builder.Host}; Database={builder.Database}; Username={builder.Username}";
        }
        catch
        {
            return "(unable to parse)";
        }
    }
}
