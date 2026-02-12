using InvoiceApi.Application.Service;
using InvoiceApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Creates TenantDbContext instances connected to the correct tenant database.
///
/// Flow:
/// 1. Get CompanyId (from ITenantResolver or explicit parameter)
/// 2. Look up CompanySystemSettings in master DB by CompanyId
/// 3. Validate: is provisioned? is active?
/// 4. Build connection string (from settings.ConnectionString or template + DatabaseName)
/// 5. Create TenantDbContext with that connection string
///
/// Connection string resolution:
/// - If CompanySystemSettings.ConnectionString is set → use it directly (tenant on different server)
/// - Otherwise → take the master connection string template and replace the database name
///   with CompanySystemSettings.DatabaseName
/// </summary>
public class TenantDbContextFactory : ITenantDbContextFactory
{
    private readonly ITenantResolver _tenantResolver;
    private readonly MasterDbContext _masterDb;
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService? _currentUserService;
    private readonly ILogger<TenantDbContextFactory> _logger;

    public TenantDbContextFactory(
        ITenantResolver tenantResolver,
        MasterDbContext masterDb,
        IConfiguration configuration,
        ILogger<TenantDbContextFactory> logger,
        ICurrentUserService? currentUserService = null)
    {
        _tenantResolver = tenantResolver;
        _masterDb = masterDb;
        _configuration = configuration;
        _logger = logger;
        _currentUserService = currentUserService;
    }

    /// <inheritdoc />
    public async Task<DbContext> CreateContextAsync(CancellationToken cancellationToken = default)
    {
        // Get the CompanyId from the current HTTP request (JWT claim or impersonation)
        var companyId = _tenantResolver.GetCurrentCompanyId();

        if (!companyId.HasValue)
        {
            throw new InvalidOperationException(
                "Cannot create tenant context: no CompanyId available. " +
                "SysAdmin users must use X-Company-Id header to specify which company to access.");
        }

        return await CreateContextForCompanyAsync(companyId.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DbContext> CreateContextForCompanyAsync(long companyId, CancellationToken cancellationToken = default)
    {
        // Look up the tenant's infrastructure settings in the master database
        var settings = await _masterDb.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
        {
            throw new InvalidOperationException(
                $"No CompanySystemSettings found for CompanyId {companyId}. " +
                "The company may not exist or hasn't been registered for multi-tenancy.");
        }

        // Validate the tenant is provisioned (database exists and has been migrated)
        if (!settings.IsProvisioned)
        {
            throw new InvalidOperationException(
                $"Tenant database for CompanyId {companyId} has not been provisioned yet. " +
                "A SysAdmin must provision the company before it can be accessed.");
        }

        // Validate the tenant is active (not suspended/deactivated)
        if (!settings.IsActive)
        {
            throw new InvalidOperationException(
                $"Tenant for CompanyId {companyId} is currently inactive (suspended). " +
                "Contact your system administrator.");
        }

        // Build the connection string for this tenant
        var connectionString = BuildConnectionString(settings);

        _logger.LogDebug(
            "Creating TenantDbContext for CompanyId {CompanyId}, Database: {DatabaseName}",
            companyId, settings.DatabaseName);

        // Create and return a new TenantDbContext connected to this tenant's database.
        // EnableRetryOnFailure handles transient SQL Server/Azure SQL errors.
        var optionsBuilder = new DbContextOptionsBuilder<TenantDbContext>();
        optionsBuilder.UseSqlServer(connectionString, b =>
            b.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null));

        return _currentUserService != null
            ? new TenantDbContext(optionsBuilder.Options, _currentUserService)
            : new TenantDbContext(optionsBuilder.Options);
    }

    /// <inheritdoc />
    public async Task<string?> GetConnectionStringAsync(long companyId, CancellationToken cancellationToken = default)
    {
        var settings = await _masterDb.CompanySystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CompanyId == companyId, cancellationToken);

        if (settings == null)
        {
            return null;
        }

        return BuildConnectionString(settings);
    }

    /// <summary>
    /// Builds the connection string for a tenant database.
    ///
    /// Two strategies:
    /// 1. If CompanySystemSettings.ConnectionString is set → use it directly
    ///    (for tenants hosted on a different SQL Server instance)
    /// 2. Otherwise → take the master connection string template from appsettings.json
    ///    and replace the Database part with the tenant's DatabaseName
    /// </summary>
    private string BuildConnectionString(Domain.Entities.CompanySystemSettings settings)
    {
        // Strategy 1: explicit connection string override
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return settings.ConnectionString;
        }

        // Strategy 2: build from master template + tenant DatabaseName
        // Read the master connection string template (same server/credentials, different DB)
        var masterConnectionString = _configuration.GetConnectionString("MasterConnection")
            ?? throw new InvalidOperationException(
                "MasterConnection not found in configuration. " +
                "Cannot build tenant connection string.");

        // Replace the Database (Initial Catalog) in the connection string with the tenant's database name.
        // SQL Server connection string format: "Server=x;Database=z;User Id=u;Password=p"
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = settings.DatabaseName
        };

        return builder.ConnectionString;
    }
}
