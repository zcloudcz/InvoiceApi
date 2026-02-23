// ============================================================================
// DiagnosticFunctions — Health check and manual migration trigger for Azure.
//
// These endpoints help diagnose deployment issues:
// - GET /api/diagnostic/health → checks DB connectivity and migration status
// - POST /api/diagnostic/migrate → manually triggers database migrations
//
// Both endpoints are anonymous (no auth required) so you can call them
// directly from a browser or curl to debug Azure deployment issues.
// IMPORTANT: Consider adding auth or removing these in production.
// ============================================================================

using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Functions.HttpFunctions;

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
        var masterConn = _configuration.GetConnectionString("MasterConnection");
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

        result["timestamp"] = DateTime.UtcNow;
        result["environment"] = Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") ?? "unknown";

        return new OkObjectResult(result);
    }

    /// <summary>
    /// Manually triggers database migrations.
    /// Call: POST https://your-function-app.azurewebsites.net/api/diagnostic/migrate
    /// </summary>
    [Function("Diagnostic_Migrate")]
    public async Task<IActionResult> Migrate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/diagnostic/migrate")] HttpRequest req)
    {
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
    /// Masks sensitive parts of a connection string for safe display.
    /// Shows server and database name, hides password.
    /// </summary>
    private static string MaskConnectionString(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return $"Server={builder.DataSource}; Database={builder.InitialCatalog}; User={builder.UserID}";
        }
        catch
        {
            return "(unable to parse)";
        }
    }
}
