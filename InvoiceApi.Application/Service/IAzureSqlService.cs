using InvoiceApi.Contracts.Dto.AzureOperation;

namespace InvoiceApi.Application.Service;

/// <summary>
/// Service for managing Azure SQL databases via the Azure Resource Manager (ARM) API.
///
/// This service handles the infrastructure layer of tenant provisioning on Azure:
/// - Creating Azure SQL databases (primarily using the free tier: GP_S_Gen5)
/// - Listing and monitoring existing databases on the Azure SQL Server
/// - Deleting databases when tenants are decommissioned
/// - Creating contained database users for Azure Function managed identity access
///
/// Authentication uses DefaultAzureCredential which supports:
/// - Managed Identity (production on Azure)
/// - Azure CLI credentials (local development)
/// - Environment variables (CI/CD pipelines)
///
/// Configuration is read from the "AzureSettings" section in appsettings.json:
/// - SubscriptionId, ResourceGroupName, SqlServerName, Location
/// - FunctionAppIdentityName (the managed identity name for Azure Functions)
///
/// Free tier limits (per subscription):
/// - Up to 10 free databases
/// - 100K vCore seconds/month per database
/// - 32GB max storage per database
/// - Auto-pause after 1 hour of inactivity (configurable)
/// </summary>
public interface IAzureSqlService
{
    /// <summary>
    /// Creates a new Azure SQL database using the ARM API.
    /// By default, creates a free tier (GP_S_Gen5) serverless database with auto-pause.
    ///
    /// The operation is idempotent — if the database already exists, it returns its current status
    /// without error (ARM CreateOrUpdate semantics).
    /// </summary>
    /// <param name="request">Database creation parameters (name, free tier flag, size limit)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Status DTO with the created database's properties</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when AzureSettings configuration is missing or invalid
    /// </exception>
    Task<AzureDatabaseStatusDto> CreateDatabaseAsync(
        AzureCreateDatabaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all databases on the configured Azure SQL Server.
    /// Includes system databases (master, tempdb, etc.) — filter by name prefix in the caller if needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of database status DTOs for all databases on the server</returns>
    Task<List<AzureDatabaseStatusDto>> ListDatabasesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current status and configuration of a specific Azure SQL database.
    /// Returns null if the database does not exist on the server.
    /// </summary>
    /// <param name="databaseName">Name of the database to check</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Database status DTO, or null if not found</returns>
    Task<AzureDatabaseStatusDto?> GetDatabaseStatusAsync(
        string databaseName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an Azure SQL database from the server.
    /// This is a DESTRUCTIVE operation — the database and all its data will be permanently removed.
    /// Azure maintains automatic backups for point-in-time restore (up to the retention period).
    /// </summary>
    /// <param name="databaseName">Name of the database to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if the database was not found</returns>
    Task<bool> DeleteDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a contained database user for the Azure Function managed identity.
    /// This allows Azure Functions to connect to the tenant database using managed identity
    /// (no passwords stored in configuration).
    ///
    /// The user is created with db_datareader and db_datawriter roles, which is sufficient
    /// for the Azure Functions to read/write tenant data but not modify schema.
    ///
    /// The identity name is read from AzureSettings:FunctionAppIdentityName configuration.
    /// The user name is sanitized (alphanumeric + hyphens + underscores only) to prevent SQL injection,
    /// since CREATE USER statements cannot use parameterized queries.
    /// </summary>
    /// <param name="connectionString">
    /// Connection string to the tenant database where the user should be created.
    /// Must use an identity with permission to create users (e.g., server admin or AAD admin).
    /// </param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CreateContainedUserAsync(
        string connectionString,
        CancellationToken cancellationToken = default);
}
