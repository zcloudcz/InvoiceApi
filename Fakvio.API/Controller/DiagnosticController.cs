using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Fakvio.Infrastructure.Service;

namespace Fakvio.API.Controller;

/// <summary>
/// Deployment diagnostics: reports database connectivity, migration status and — most
/// importantly for a database rollout — WHICH authentication mode the process actually
/// resolved and WHERE that value came from.
///
/// This controller is the single source of truth for the health payload.
///
/// SECURITY: SysAdmin only. The payload names the database host/user (masked connection
/// string) and lists migration names — harmless to an operator, useful reconnaissance to
/// anyone else. Passwords and access tokens are never part of it: the connection string
/// only ever leaves through <see cref="MaskConnectionString"/>, which rebuilds the string
/// from three whitelisted fields instead of redacting a blacklist.
/// </summary>
[ApiController]
[Route("api/diagnostic")]
[Produces("application/json")]
[Authorize(Roles = "SysAdmin")]
public class DiagnosticController : ControllerBase
{
    private readonly MasterDbContext _masterDb;
    private readonly DatabaseOptions _databaseOptions;
    private readonly ILogger<DiagnosticController> _logger;

    public DiagnosticController(
        MasterDbContext masterDb,
        DatabaseOptions databaseOptions,
        ILogger<DiagnosticController> logger)
    {
        _masterDb = masterDb;
        _databaseOptions = databaseOptions;
        _logger = logger;
    }

    /// <summary>
    /// Health check — reports the resolved database auth mode plus connectivity and
    /// migration status of the master database.
    ///
    /// Returns 200 when the master database is reachable, 503 when it is not, so an
    /// uptime probe can rely on the status code alone.
    /// </summary>
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Health(CancellationToken ct = default)
    {
        var result = new Dictionary<string, object>();

        // Connection string: presence + masked form only. Never the raw value.
        //
        // Taken from the SAME resolved DatabaseOptions the data source was built from, not
        // from IConfiguration. Reading "ConnectionStrings:DefaultConnection" directly used to
        // let the payload contradict itself: "Database:ConnectionString" wins in
        // DatabaseOptions.Resolve, so a deployment configured only through that key reported
        // masterConnectionConfigured=false right next to masterDbCanConnect=true.
        var masterConn = _databaseOptions.ConnectionString;
        result["masterConnectionConfigured"] = !string.IsNullOrEmpty(masterConn);
        if (!string.IsNullOrEmpty(masterConn))
        {
            result["masterConnectionServer"] = MaskConnectionString(masterConn);
        }

        // The two fields this endpoint exists for. DatabaseOptions is the singleton the
        // composition root resolved at startup, so these report what the process is REALLY
        // running with — not what the config files happen to say now. AuthModeSource names
        // the winning key ("Database:AuthMode" / "UseAzureAdAuthentication (legacy)" /
        // "default"), which is what makes an Azure App Settings rollout verifiable.
        result["authMode"] = _databaseOptions.AuthMode.ToString();
        result["authModeSource"] = _databaseOptions.AuthModeSource;

        // Startup outcome. The master migration failure is tolerated at startup (the host keeps
        // serving), so this is where an operator sees that the schema is behind. Reported, never
        // used to gate: the process can serve traffic perfectly well while being two migrations behind.
        result["startupMigration"] = StartupState.MigrationSucceeded switch
        {
            true => "succeeded",
            false => "failed",
            null => "pending"
        };
        if (StartupState.MigrationCompletedAt is { } completedAt)
        {
            result["startupMigrationCompletedAt"] = completedAt;
        }
        if (StartupState.MigrationError is { } migrationError)
        {
            result["startupMigrationError"] = migrationError;
        }

        await ProbeMasterDatabaseAsync(result, ct);

        // Top-level flags: the two fields a monitoring dashboard should watch.
        var isConnected = result.TryGetValue("masterDbCanConnect", out var canConn) && canConn is true;
        var hasPending = result.TryGetValue("masterDbPendingMigrations", out var pending) && pending is int p && p > 0;
        result["databaseConnected"] = isConnected;
        result["databaseReady"] = isConnected && !hasPending;

        result["timestamp"] = DateTime.UtcNow;
        // Both hosts are served from here, so both environment variables are consulted:
        // ASPNETCORE_ENVIRONMENT is set by the App Service settings (Production) or by
        // launchSettings.json locally.
        result["environment"] =
            Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "unknown";

        return isConnected
            ? Ok(result)
            : new ObjectResult(result) { StatusCode = StatusCodes.Status503ServiceUnavailable };
    }

    /// <summary>
    /// Fills in the master-database part of the report. Every failure is caught and
    /// reported as a field instead of thrown: a health endpoint that returns 500 tells
    /// the operator nothing about WHY the database is unreachable.
    /// </summary>
    private async Task ProbeMasterDatabaseAsync(Dictionary<string, object> result, CancellationToken ct)
    {
        try
        {
            var canConnect = await _masterDb.Database.CanConnectAsync(ct);
            result["masterDbCanConnect"] = canConnect;
            if (!canConnect)
            {
                return;
            }

            var pending = await _masterDb.Database.GetPendingMigrationsAsync(ct);
            var applied = await _masterDb.Database.GetAppliedMigrationsAsync(ct);
            result["masterDbAppliedMigrations"] = applied.Count();
            result["masterDbPendingMigrations"] = pending.Count();
            result["masterDbPendingMigrationNames"] = pending.ToList();

            // Counting users proves the master schema is not just reachable but actually
            // migrated — a fresh, empty database answers CanConnect happily.
            try
            {
                result["masterDbUserCount"] = await _masterDb.User.CountAsync(ct);
            }
            catch (Exception ex)
            {
                result["masterDbUserTableError"] = ex.Message;
            }
        }
        catch (Exception ex)
        {
            result["masterDbError"] = ex.Message;
            _logger.LogError(ex, "Health check: Master DB connection failed");
        }
    }

    /// <summary>
    /// Rebuilds the connection string from the three non-secret fields worth showing
    /// (host, database, username). Whitelisting rather than redacting means a newly
    /// added secret-bearing keyword can never leak by omission.
    /// </summary>
    private static string MaskConnectionString(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"Host={builder.Host}; Database={builder.Database}; Username={builder.Username}";
        }
        catch
        {
            return "(unable to parse)";
        }
    }
}
