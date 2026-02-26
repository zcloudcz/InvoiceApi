using InvoiceApi.Application.Service;
using InvoiceApi.Contracts.Dto.AzureOperation;
using InvoiceApi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace InvoiceApi.API.Controller;

/// <summary>
/// Controller for Azure SQL database operations and full tenant provisioning on Azure.
///
/// Provides SysAdmin endpoints to:
/// - Create Azure SQL databases (free tier or paid) via the ARM API
/// - Provision complete tenants (create Azure DB → create contained user → run full provisioning)
/// - List, monitor, and delete Azure SQL databases
///
/// Full provisioning flow (POST /api/azure-operation/provision-tenant):
/// 1. Validates the company and settings exist in master DB
/// 2. Creates Azure SQL database via ARM API (CreateOrUpdate, idempotent)
/// 3. Builds connection string for the new database
/// 4. Creates contained user for Azure Function managed identity (azFunction)
/// 5. Updates CompanySystemSettings with the Azure SQL connection string
/// 6. Runs TenantProvisioningService (migrations, code table copy, issuer, number sequences)
/// 7. Returns the Azure database status
///
/// IMPORTANT: All endpoints require SysAdmin role.
/// Azure credentials are resolved via DefaultAzureCredential or service principal config.
/// </summary>
[ApiController]
[Route("api/azure-operation")]
[Authorize(Roles = "SysAdmin")]
public class AzureOperationController : ControllerBase
{
    private readonly IAzureSqlService _azureSqlService;
    private readonly ITenantProvisioningService _provisioningService;
    private readonly MasterDbContext _masterContext;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureOperationController> _logger;

    public AzureOperationController(
        IAzureSqlService azureSqlService,
        ITenantProvisioningService provisioningService,
        MasterDbContext masterContext,
        IConfiguration configuration,
        ILogger<AzureOperationController> logger)
    {
        _azureSqlService = azureSqlService;
        _provisioningService = provisioningService;
        _masterContext = masterContext;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Full tenant provisioning flow: creates an Azure SQL database, sets up contained user
    /// for Azure Functions managed identity, and runs the complete provisioning pipeline
    /// (migrations, code tables, issuer, number sequences).
    ///
    /// This is the primary endpoint for onboarding a new tenant on Azure SQL.
    /// The entire flow is idempotent — safe to re-run after partial failures.
    /// </summary>
    /// <param name="request">Database name, company ID, and Azure SQL tier settings</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Azure database status DTO on success</returns>
    /// <response code="200">Tenant provisioned successfully</response>
    /// <response code="400">Invalid request (missing company, already provisioned, etc.)</response>
    /// <response code="500">Azure ARM API or provisioning error</response>
    [HttpPost("provision-tenant")]
    [ProducesResponseType(typeof(AzureDatabaseStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<AzureDatabaseStatusDto>> ProvisionTenant(
        [FromBody] AzureCreateDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Starting full Azure tenant provisioning for company {CompanyId} (database: {DatabaseName})",
                request.CompanyId, request.DatabaseName);

            // ── Step 1: Validate company and settings exist ──────────────────
            var settings = await _masterContext.CompanySystemSettings
                .FirstOrDefaultAsync(s => s.CompanyId == request.CompanyId, cancellationToken);

            if (settings == null)
            {
                _logger.LogWarning(
                    "CompanySystemSettings not found for company {CompanyId}", request.CompanyId);
                return BadRequest(new { message = $"CompanySystemSettings not found for company {request.CompanyId}. Create settings first." });
            }

            // Verify the company (Client with IsIssuer) actually exists
            var companyExists = await _masterContext.Client
                .AsNoTracking()
                .AnyAsync(c => c.Id == request.CompanyId && c.IsIssuer, cancellationToken);

            if (!companyExists)
            {
                _logger.LogWarning("Company {CompanyId} not found or is not an issuer", request.CompanyId);
                return BadRequest(new { message = $"Company {request.CompanyId} not found or is not marked as issuer." });
            }

            // ── Step 2: Create Azure SQL database via ARM API ────────────────
            _logger.LogInformation("Step 2: Creating Azure SQL database '{DatabaseName}'", request.DatabaseName);
            var dbStatus = await _azureSqlService.CreateDatabaseAsync(request, cancellationToken);

            // ── Step 3: Build connection string for the new Azure SQL database ─
            var sqlServerName = _configuration["AzureSettings:SqlServerName"]
                ?? throw new InvalidOperationException("AzureSettings:SqlServerName is not configured.");

            // Azure SQL connection string format:
            // Server=tcp:{server}.database.windows.net,1433 — Azure SQL always uses TCP on port 1433
            // Encrypt=True — mandatory for Azure SQL (TLS encryption)
            // TrustServerCertificate=False — validate the Azure SSL certificate
            // Authentication=Active Directory Default — uses DefaultAzureCredential chain
            var connBuilder = new SqlConnectionStringBuilder
            {
                DataSource = $"tcp:{sqlServerName}.database.windows.net,1433",
                InitialCatalog = request.DatabaseName,
                Encrypt = true,
                TrustServerCertificate = false,
                Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault
            };
            var azureConnectionString = connBuilder.ConnectionString;

            _logger.LogInformation("Step 3: Built Azure SQL connection string for '{DatabaseName}'", request.DatabaseName);

            // ── Step 4: Create contained user for Azure Function managed identity ─
            _logger.LogInformation("Step 4: Creating contained user in '{DatabaseName}'", request.DatabaseName);
            try
            {
                await _azureSqlService.CreateContainedUserAsync(azureConnectionString, cancellationToken);
            }
            catch (Exception ex)
            {
                // Log but don't fail the entire flow — the user can be created later manually.
                // This might fail if AAD admin is not configured on the SQL Server.
                _logger.LogWarning(ex,
                    "Failed to create contained user in '{DatabaseName}'. " +
                    "Continuing with provisioning — the user can be created manually later.",
                    request.DatabaseName);
            }

            // ── Step 5: Update CompanySystemSettings with Azure SQL connection string ─
            settings.DatabaseName = request.DatabaseName;
            settings.ConnectionString = azureConnectionString;
            await _masterContext.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "Step 5: Updated CompanySystemSettings for company {CompanyId} with Azure SQL connection",
                request.CompanyId);

            // ── Step 6: Run full provisioning (migrations, code tables, issuer, sequences) ─
            _logger.LogInformation("Step 6: Running TenantProvisioningService for company {CompanyId}", request.CompanyId);
            await _provisioningService.ProvisionTenantAsync(request.CompanyId, cancellationToken);

            _logger.LogInformation(
                "Full Azure tenant provisioning completed for company {CompanyId} (database: {DatabaseName})",
                request.CompanyId, request.DatabaseName);

            return Ok(dbStatus);
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
                "Unexpected error during Azure tenant provisioning for company {CompanyId}",
                request.CompanyId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An unexpected error occurred during tenant provisioning. Check server logs." });
        }
    }

    /// <summary>
    /// Creates a standalone Azure SQL database without running the full provisioning pipeline.
    /// Use this when you need to create the database first and provision later (e.g., for testing).
    /// For full onboarding, use POST /api/azure-operation/provision-tenant instead.
    /// </summary>
    /// <param name="request">Database creation parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Azure database status DTO</returns>
    /// <response code="201">Database created successfully</response>
    /// <response code="400">Invalid request parameters</response>
    /// <response code="500">Azure ARM API error</response>
    [HttpPost("create-database")]
    [ProducesResponseType(typeof(AzureDatabaseStatusDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<AzureDatabaseStatusDto>> CreateDatabase(
        [FromBody] AzureCreateDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Creating standalone Azure SQL database '{DatabaseName}' (FreeTier={UseFreeOffer})",
                request.DatabaseName, request.UseFreeOffer);

            var result = await _azureSqlService.CreateDatabaseAsync(request, cancellationToken);

            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create database '{DatabaseName}': {Message}",
                request.DatabaseName, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error creating Azure SQL database '{DatabaseName}'",
                request.DatabaseName);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while creating the Azure SQL database." });
        }
    }

    /// <summary>
    /// Lists all databases on the configured Azure SQL Server.
    /// Returns both system databases (master, tempdb) and tenant databases.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of all Azure SQL database statuses</returns>
    /// <response code="200">Returns list of databases</response>
    /// <response code="500">Azure ARM API error</response>
    [HttpGet("databases")]
    [ProducesResponseType(typeof(List<AzureDatabaseStatusDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<List<AzureDatabaseStatusDto>>> ListDatabases(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var databases = await _azureSqlService.ListDatabasesAsync(cancellationToken);
            return Ok(databases);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing Azure SQL databases");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An error occurred while listing Azure SQL databases." });
        }
    }

    /// <summary>
    /// Gets the current status and configuration of a specific Azure SQL database.
    /// Useful for checking if a database is online, paused, or in a transition state.
    /// </summary>
    /// <param name="name">Database name to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Database status DTO</returns>
    /// <response code="200">Returns database status</response>
    /// <response code="404">Database not found</response>
    /// <response code="500">Azure ARM API error</response>
    [HttpGet("databases/{name}/status")]
    [ProducesResponseType(typeof(AzureDatabaseStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<AzureDatabaseStatusDto>> GetDatabaseStatus(
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _azureSqlService.GetDatabaseStatusAsync(name, cancellationToken);

            if (status == null)
                return NotFound(new { message = $"Database '{name}' not found on the Azure SQL Server." });

            return Ok(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status of Azure SQL database '{DatabaseName}'", name);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"An error occurred while checking database '{name}'." });
        }
    }

    /// <summary>
    /// Deletes an Azure SQL database from the server.
    /// WARNING: This is a DESTRUCTIVE operation. The database and all data will be permanently removed.
    /// Azure maintains automatic backups for point-in-time restore up to the configured retention period.
    ///
    /// This does NOT update CompanySystemSettings — the SysAdmin should manually deactivate
    /// the tenant via CompanyController before deleting the database.
    /// </summary>
    /// <param name="name">Database name to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>No content on success</returns>
    /// <response code="204">Database deleted successfully</response>
    /// <response code="404">Database not found</response>
    /// <response code="500">Azure ARM API error</response>
    [HttpDelete("databases/{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteDatabase(
        string name,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogWarning("SysAdmin requested deletion of Azure SQL database '{DatabaseName}'", name);

            var deleted = await _azureSqlService.DeleteDatabaseAsync(name, cancellationToken);

            if (!deleted)
                return NotFound(new { message = $"Database '{name}' not found on the Azure SQL Server." });

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting Azure SQL database '{DatabaseName}'", name);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = $"An error occurred while deleting database '{name}'." });
        }
    }
}
