using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.AzureOperation;
using Fakvio.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for PostgreSQL tenant schema operations and full tenant provisioning.
///
/// In the single-database multi-schema architecture, each tenant gets an isolated
/// PostgreSQL schema (e.g., "tenant_42") within a shared database. This controller
/// provides SysAdmin endpoints to manage those schemas:
///
/// - Provision a new tenant schema (CREATE SCHEMA + migrations + seed data)
/// - List all tenant schemas with their provisioning status
/// - Check the status of a specific tenant schema
/// - Drop a tenant schema (CASCADE) and mark it as unprovisioned
///
/// Full provisioning flow (POST /api/tenant-operation/provision):
/// 1. Validates the company and CompanySystemSettings exist in master DB
/// 2. Creates the PostgreSQL schema via TenantProvisioningService
/// 3. Applies EF Core migrations to the new schema
/// 4. Copies code tables from master (VatRate, Currency, etc.)
/// 5. Creates issuer + default number sequences in the tenant schema
/// 6. Returns the tenant schema status DTO
///
/// IMPORTANT: All endpoints require SysAdmin role.
/// Schema names follow the convention "tenant_{companyId}" and use only lowercase
/// alphanumeric characters and underscores (PostgreSQL naming rules).
/// </summary>
[ApiController]
[Route("api/tenant-operation")]
[Authorize(Roles = "SysAdmin")]
public class TenantOperationController : ControllerBase
{
    // Service that handles the full provisioning pipeline
    // (schema creation, migrations, code table copy, issuer, number sequences)
    private readonly ITenantProvisioningService _provisioningService;

    // Master database context for reading CompanySystemSettings
    // (tenant metadata like schema name, provisioning status, etc.)
    private readonly MasterDbContext _masterContext;

    // Configuration for reading the shared PostgreSQL connection string
    private readonly IConfiguration _configuration;

    // Shared NpgsqlDataSource — creates connections with Azure AD token auth support.
    // ALWAYS use _dataSource.OpenConnectionAsync() instead of new NpgsqlConnection().
    private readonly NpgsqlDataSource _dataSource;

    // Structured logger for tracing provisioning operations
    private readonly ILogger<TenantOperationController> _logger;

    /// <summary>
    /// Constructor with dependency injection.
    /// All dependencies are registered in DI container at startup.
    /// </summary>
    public TenantOperationController(
        ITenantProvisioningService provisioningService,
        MasterDbContext masterContext,
        IConfiguration configuration,
        NpgsqlDataSource dataSource,
        ILogger<TenantOperationController> logger)
    {
        _provisioningService = provisioningService;
        _masterContext = masterContext;
        _configuration = configuration;
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>
    /// Full tenant provisioning: creates a PostgreSQL schema for the tenant,
    /// applies EF Core migrations, copies code tables, and seeds initial data.
    ///
    /// This is the primary endpoint for onboarding a new tenant.
    /// The entire flow is idempotent for schema creation (CREATE SCHEMA IF NOT EXISTS),
    /// but will fail if the tenant is already marked as provisioned.
    /// </summary>
    /// <param name="request">
    /// Contains CompanyId (the master DB company to provision) and SchemaName
    /// (the PostgreSQL schema name, e.g., "tenant_42").
    /// </param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>TenantSchemaStatusDto with the new schema's provisioning status</returns>
    /// <response code="200">Tenant schema provisioned successfully</response>
    /// <response code="400">Invalid request (missing company, already provisioned, etc.)</response>
    /// <response code="500">Schema creation or provisioning pipeline error</response>
    [HttpPost("provision")]
    [ProducesResponseType(typeof(TenantSchemaStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TenantSchemaStatusDto>> ProvisionTenant(
        [FromBody] CreateTenantSchemaRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Starting tenant schema provisioning for company {CompanyId} (schema: {SchemaName})",
                request.CompanyId, request.SchemaName);

            // ── Step 1: Validate company and settings exist in master DB ──────
            // CompanySystemSettings must be created before provisioning can begin.
            // This ensures the tenant has been registered in the system.
            var settings = await _masterContext.CompanySystemSettings
                .Include(s => s.Company) // Include company nav prop for the response DTO
                .FirstOrDefaultAsync(s => s.CompanyId == request.CompanyId, cancellationToken);

            if (settings == null)
            {
                _logger.LogWarning(
                    "CompanySystemSettings not found for company {CompanyId}", request.CompanyId);
                return BadRequest(new
                {
                    message = $"CompanySystemSettings not found for company {request.CompanyId}. " +
                              "Create settings first via POST /api/company/{{id}}/settings."
                });
            }

            // Verify the company (Client with IsIssuer) actually exists in master DB
            var companyExists = await _masterContext.Client
                .AsNoTracking()
                .AnyAsync(c => c.Id == request.CompanyId && c.IsIssuer, cancellationToken);

            if (!companyExists)
            {
                _logger.LogWarning("Company {CompanyId} not found or is not an issuer", request.CompanyId);
                return BadRequest(new { message = $"Company {request.CompanyId} not found or is not marked as issuer." });
            }

            // ── Step 2: Update schema name on settings if provided ───────────
            // The request may carry a specific schema name; update settings before provisioning.
            if (!string.IsNullOrWhiteSpace(request.SchemaName))
            {
                settings.SchemaName = request.SchemaName;
                await _masterContext.SaveChangesAsync(cancellationToken);
            }

            // ── Step 3: Run full provisioning pipeline ───────────────────────
            // TenantProvisioningService handles:
            //   a) CREATE SCHEMA IF NOT EXISTS
            //   b) EF Core migrations on the new schema
            //   c) Code table copy from master (VatRate, Currency, etc.)
            //   d) Issuer record creation in tenant schema
            //   e) Default number sequences for Invoice and CreditNote
            //   f) Marks IsProvisioned = true with ProvisionedAt timestamp
            _logger.LogInformation(
                "Running TenantProvisioningService for company {CompanyId} (schema: {SchemaName})",
                request.CompanyId, settings.SchemaName);

            await _provisioningService.ProvisionTenantAsync(request.CompanyId, cancellationToken);

            // Reload settings after provisioning (IsProvisioned/ProvisionedAt may have changed)
            await _masterContext.Entry(settings).ReloadAsync(cancellationToken);

            _logger.LogInformation(
                "Tenant schema provisioning completed for company {CompanyId} (schema: {SchemaName})",
                request.CompanyId, settings.SchemaName);

            // Map the updated settings to the response DTO
            return Ok(MapToSchemaStatusDto(settings));
        }
        catch (InvalidOperationException ex)
        {
            // Configuration or validation errors — client-actionable
            _logger.LogWarning(ex, "Provisioning failed for company {CompanyId}: {Message}",
                request.CompanyId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during tenant schema provisioning for company {CompanyId}",
                request.CompanyId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An unexpected error occurred during tenant provisioning. Check server logs." });
        }
    }

    /// <summary>
    /// Lists all tenant schemas registered in the master database.
    /// Returns CompanySystemSettings data for every company, including
    /// provisioned, unprovisioned, active, and inactive tenants.
    ///
    /// This is useful for SysAdmin dashboards to see the overall
    /// multi-tenant landscape at a glance.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>List of TenantSchemaStatusDto for all registered companies</returns>
    /// <response code="200">Returns list of tenant schema statuses</response>
    /// <response code="500">Database query error</response>
    [HttpGet("list")]
    [ProducesResponseType(typeof(List<TenantSchemaStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<List<TenantSchemaStatusDto>>> ListTenantSchemas(
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Load all CompanySystemSettings with their Company navigation property.
            // AsNoTracking() is used because we only need read access here.
            var allSettings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .Include(s => s.Company)
                .OrderBy(s => s.CompanyId)
                .ToListAsync(cancellationToken);

            // Map each settings record to the response DTO
            var result = allSettings.Select(MapToSchemaStatusDto).ToList();

            _logger.LogInformation("Listed {Count} tenant schemas", result.Count);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing tenant schemas");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while listing tenant schemas." });
        }
    }

    /// <summary>
    /// Gets the current status of a specific tenant schema by company ID.
    /// Returns provisioning state, activity status, and schema metadata.
    ///
    /// Useful for checking if a specific tenant has been provisioned
    /// and is ready to accept connections.
    /// </summary>
    /// <param name="companyId">The master DB company ID to check</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>TenantSchemaStatusDto for the specified company</returns>
    /// <response code="200">Returns tenant schema status</response>
    /// <response code="404">CompanySystemSettings not found for the given company ID</response>
    /// <response code="500">Database query error</response>
    [HttpGet("status/{companyId:long}")]
    [ProducesResponseType(typeof(TenantSchemaStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TenantSchemaStatusDto>> GetTenantSchemaStatus(
        long companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Look up the tenant settings by company ID (not by schema name)
            var settings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .Include(s => s.Company)
                .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

            if (settings == null)
            {
                return NotFound(new
                {
                    message = $"No CompanySystemSettings found for company {companyId}."
                });
            }

            return Ok(MapToSchemaStatusDto(settings));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenant schema status for company {CompanyId}", companyId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"An error occurred while checking tenant schema status for company {companyId}." });
        }
    }

    /// <summary>
    /// Drops a tenant schema (CASCADE) and marks the tenant as unprovisioned.
    ///
    /// WARNING: This is a DESTRUCTIVE operation. The schema and ALL its data
    /// (tables, views, sequences, functions) will be permanently removed.
    /// PostgreSQL DROP SCHEMA ... CASCADE removes everything in the schema.
    ///
    /// After dropping, CompanySystemSettings is updated:
    /// - IsProvisioned = false
    /// - IsActive = false
    /// - SchemaName is preserved (for audit trail)
    ///
    /// The CompanySystemSettings record itself is NOT deleted — the company
    /// can be re-provisioned later if needed.
    /// </summary>
    /// <param name="companyId">The master DB company ID whose schema to drop</param>
    /// <param name="cancellationToken">Cancellation token for async operations</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Schema dropped and tenant marked as unprovisioned</response>
    /// <response code="404">CompanySystemSettings not found for the given company ID</response>
    /// <response code="400">Schema name is empty (nothing to drop)</response>
    /// <response code="500">Schema drop or database error</response>
    [HttpDelete("{companyId:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteTenantSchema(
        long companyId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ── Step 1: Find the tenant's settings in master DB ──────────────
            var settings = await _masterContext.CompanySystemSettings
                .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

            if (settings == null)
            {
                return NotFound(new
                {
                    message = $"No CompanySystemSettings found for company {companyId}."
                });
            }

            if (string.IsNullOrWhiteSpace(settings.SchemaName))
            {
                return BadRequest(new
                {
                    message = $"Company {companyId} has no schema name configured — nothing to drop."
                });
            }

            _logger.LogWarning(
                "SysAdmin requested deletion of tenant schema '{SchemaName}' for company {CompanyId} " +
                "— this is a DESTRUCTIVE operation",
                settings.SchemaName, companyId);

            // ── Step 2: Drop the PostgreSQL schema with CASCADE ──────────────
            // CASCADE removes all objects in the schema (tables, views, sequences, etc.).
            // Use NpgsqlDataSource for Azure AD token auth support.
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

            // Use a parameterized-safe approach: schema names can't use @parameters in DDL,
            // but we validate the schema name format (alphanumeric + underscore only)
            // and quote it with double quotes to prevent SQL injection.
            var schemaName = settings.SchemaName;

            // Validate schema name format — extra safety layer against SQL injection
            // (CompanySystemSettings.SchemaName should already be validated at creation time)
            if (!System.Text.RegularExpressions.Regex.IsMatch(schemaName, @"^[a-z][a-z0-9_]*$"))
            {
                return BadRequest(new
                {
                    message = $"Schema name '{schemaName}' has invalid format. " +
                              "Must start with lowercase letter and contain only lowercase letters, numbers, and underscores."
                });
            }

            // DROP SCHEMA IF EXISTS ... CASCADE:
            // - IF EXISTS prevents errors if schema was already manually dropped
            // - CASCADE removes all dependent objects (tables, views, sequences, etc.)
            // - Schema name is quoted with double quotes for PostgreSQL identifier safety
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"DROP SCHEMA IF EXISTS \"{schemaName}\" CASCADE";
            await cmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation(
                "Dropped PostgreSQL schema '{SchemaName}' for company {CompanyId}",
                schemaName, companyId);

            // ── Step 3: Update CompanySystemSettings in master DB ─────────────
            // Mark tenant as unprovisioned and inactive.
            // SchemaName is preserved so SysAdmin can see which schema was dropped.
            settings.IsProvisioned = false;
            settings.IsActive = false;
            settings.UpdatedAt = DateTime.UtcNow;
            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Marked company {CompanyId} as unprovisioned after schema drop", companyId);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Schema deletion failed for company {CompanyId}: {Message}",
                companyId, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error during tenant schema deletion for company {CompanyId}",
                companyId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An unexpected error occurred while dropping the tenant schema. Check server logs." });
        }
    }

    /// <summary>
    /// Fixes schema permissions for ALL provisioned tenant schemas.
    ///
    /// This endpoint retroactively applies GRANT ALL + ALTER DEFAULT PRIVILEGES
    /// on every provisioned tenant schema, ensuring the current Azure (Entra ID) user
    /// has full access to all existing AND future objects (tables, sequences, functions).
    ///
    /// USE CASE: Run this once after deploying the permissions fix, to update schemas
    /// that were provisioned BEFORE the fix was added to the provisioning pipeline.
    /// Safe to run multiple times — PostgreSQL silently ignores duplicate grants.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Summary of schemas updated</returns>
    /// <response code="200">Permissions fixed for all schemas</response>
    /// <response code="500">Error applying permissions</response>
    [HttpPost("fix-permissions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> FixSchemaPermissions(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("SysAdmin requested schema permissions fix for all provisioned tenants");

            // Load all provisioned tenant schemas from master DB
            var provisionedSettings = await _masterContext.CompanySystemSettings
                .AsNoTracking()
                .Where(s => s.IsProvisioned && !string.IsNullOrEmpty(s.SchemaName))
                .ToListAsync(cancellationToken);

            if (provisionedSettings.Count == 0)
            {
                return Ok(new { message = "No provisioned schemas found.", schemasFixed = 0 });
            }

            // Open a single connection via NpgsqlDataSource — supports Azure AD token auth.
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

            // Resolve current database user once (AAD principal on Azure, password user locally)
            await using var userCmd = connection.CreateCommand();
            userCmd.CommandText = "SELECT CURRENT_USER";
            var currentUser = (string)(await userCmd.ExecuteScalarAsync(cancellationToken))!;

            var fixedSchemas = new List<string>();
            var failedSchemas = new List<string>();

            foreach (var settings in provisionedSettings)
            {
                try
                {
                    var safeName = new string(settings.SchemaName
                        .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();

                    if (string.IsNullOrEmpty(safeName)) continue;

                    // Grant ALL on schema itself (USAGE + CREATE)
                    await ExecuteNonQueryAsync(connection,
                        $"GRANT ALL ON SCHEMA \"{safeName}\" TO \"{currentUser}\"", cancellationToken);

                    // Grant ALL on existing tables and sequences
                    await ExecuteNonQueryAsync(connection,
                        $"GRANT ALL ON ALL TABLES IN SCHEMA \"{safeName}\" TO \"{currentUser}\"", cancellationToken);
                    await ExecuteNonQueryAsync(connection,
                        $"GRANT ALL ON ALL SEQUENCES IN SCHEMA \"{safeName}\" TO \"{currentUser}\"", cancellationToken);

                    // ALTER DEFAULT PRIVILEGES for future objects (tables, sequences, functions)
                    await ExecuteNonQueryAsync(connection,
                        $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{safeName}\" GRANT ALL ON TABLES TO \"{currentUser}\"", cancellationToken);
                    await ExecuteNonQueryAsync(connection,
                        $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{safeName}\" GRANT ALL ON SEQUENCES TO \"{currentUser}\"", cancellationToken);
                    await ExecuteNonQueryAsync(connection,
                        $"ALTER DEFAULT PRIVILEGES IN SCHEMA \"{safeName}\" GRANT ALL ON FUNCTIONS TO \"{currentUser}\"", cancellationToken);

                    fixedSchemas.Add(safeName);
                    _logger.LogInformation("Fixed permissions for schema '{Schema}' → user '{User}'",
                        safeName, currentUser);
                }
                catch (Exception ex)
                {
                    failedSchemas.Add(settings.SchemaName);
                    _logger.LogError(ex, "Failed to fix permissions for schema '{Schema}'", settings.SchemaName);
                }
            }

            _logger.LogInformation(
                "Schema permissions fix complete: {Fixed} fixed, {Failed} failed",
                fixedSchemas.Count, failedSchemas.Count);

            return Ok(new
            {
                message = $"Permissions fixed for {fixedSchemas.Count} schemas.",
                currentUser,
                schemasFixed = fixedSchemas.Count,
                schemasFailed = failedSchemas.Count,
                fixedSchemas,
                failedSchemas
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during schema permissions fix");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while fixing schema permissions. Check server logs." });
        }
    }

    #region Private helpers

    /// <summary>
    /// Executes a non-query SQL command on an open connection.
    /// Helper to avoid repeating the create-command-execute pattern.
    /// </summary>
    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Maps a CompanySystemSettings entity to a TenantSchemaStatusDto for API responses.
    /// Extracts only the properties relevant for SysAdmin monitoring of tenant schemas.
    /// </summary>
    /// <param name="settings">The CompanySystemSettings entity (with Company navigation loaded)</param>
    /// <returns>A TenantSchemaStatusDto populated from the entity</returns>
    private static TenantSchemaStatusDto MapToSchemaStatusDto(
        Fakvio.Domain.Entities.CompanySystemSettings settings)
    {
        return new TenantSchemaStatusDto
        {
            SchemaName = settings.SchemaName,
            CompanyId = settings.CompanyId,
            CompanyName = settings.Company?.CompanyName ?? "Unknown",
            IsProvisioned = settings.IsProvisioned,
            IsActive = settings.IsActive,
            CreatedAt = settings.ProvisionedAt.HasValue
                ? new DateTimeOffset(settings.ProvisionedAt.Value, TimeSpan.Zero)
                : null,
            LastMigratedAt = settings.UpdatedAt.HasValue
                ? new DateTimeOffset(settings.UpdatedAt.Value, TimeSpan.Zero)
                : null
        };
    }

    #endregion
}
