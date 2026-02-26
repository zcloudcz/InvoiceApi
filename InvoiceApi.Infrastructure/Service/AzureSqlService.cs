using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Sql;
using Azure.ResourceManager.Sql.Models;
using InvoiceApi.Application.Service;
using InvoiceApi.Contracts.Dto.AzureOperation;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// Implementation of IAzureSqlService using the Azure Resource Manager (ARM) SDK.
///
/// Manages Azure SQL databases for the multi-tenant architecture:
/// - Creates databases via ARM API (primarily free tier GP_S_Gen5)
/// - Lists/monitors databases on the configured Azure SQL Server
/// - Creates contained users for Azure Function managed identity access
///
/// Authentication flow:
/// 1. Reads optional TenantId/ClientId/ClientSecret from AzureSettings config
/// 2. If all three are present → uses ClientSecretCredential (service principal)
/// 3. Otherwise → uses DefaultAzureCredential (managed identity, Azure CLI, etc.)
///
/// Required NuGet packages:
/// - Azure.ResourceManager (ARM client)
/// - Azure.ResourceManager.Sql (SQL Server resource operations)
/// - Azure.Identity (authentication via DefaultAzureCredential)
/// </summary>
public partial class AzureSqlService : IAzureSqlService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<AzureSqlService> _logger;

    public AzureSqlService(
        IConfiguration configuration,
        ILogger<AzureSqlService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Compiled regex for sanitizing identity names used in SQL statements.
    /// Only allows alphanumeric characters, hyphens, and underscores.
    /// This prevents SQL injection in CREATE USER statements which cannot use parameters.
    /// </summary>
    [GeneratedRegex(@"^[a-zA-Z0-9\-_]+$")]
    private static partial Regex SafeIdentityNameRegex();

    /// <inheritdoc />
    public async Task<AzureDatabaseStatusDto> CreateDatabaseAsync(
        AzureCreateDatabaseRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Creating Azure SQL database '{DatabaseName}' (FreeTier={UseFreeOffer})",
            request.DatabaseName, request.UseFreeOffer);

        // Get the SQL databases collection from the Azure SQL Server resource
        var sqlDatabases = await GetSqlDatabaseCollectionAsync(cancellationToken);
        var location = GetRequiredSetting("AzureSettings:Location");

        // Configure the database properties based on the request
        var dbData = new SqlDatabaseData(new Azure.Core.AzureLocation(location))
        {
            // GP_S_Gen5 = General Purpose Serverless Gen5 — the SKU used for Azure SQL free tier.
            // Capacity = 1 means 1 vCore (minimum for serverless, auto-scales up to the limit).
            Sku = new SqlSku("GP_S_Gen5")
            {
                Tier = "GeneralPurpose",
                Family = "Gen5",
                Capacity = 1
            },
            // Maximum storage size in bytes (default 32GB for free tier)
            MaxSizeBytes = request.MaxSizeBytes
        };

        // Configure free tier settings if requested
        if (request.UseFreeOffer)
        {
            // UseFreeLimit = true activates the Azure SQL free offer (100K vCore seconds/month)
            dbData.UseFreeLimit = true;

            // FreeLimitExhaustionBehavior controls what happens when free credits run out:
            // - AutoPause: database pauses until next billing cycle (no charges incurred)
            // - BillOverUsage: database keeps running and you pay standard rates for overage
            dbData.FreeLimitExhaustionBehavior = request.FreeLimitExhaustionBehavior == "BillOverUsage"
                ? FreeLimitExhaustionBehavior.BillOverUsage
                : FreeLimitExhaustionBehavior.AutoPause;
        }

        // ARM CreateOrUpdate is idempotent — if database already exists, it updates settings.
        // WaitForCompletion() blocks until the long-running operation finishes (typically 1-3 minutes).
        var operation = await sqlDatabases.CreateOrUpdateAsync(
            WaitUntil.Completed,
            request.DatabaseName,
            dbData,
            cancellationToken);

        var dbResource = operation.Value;

        _logger.LogInformation(
            "Azure SQL database '{DatabaseName}' created successfully (Status={Status}, SKU={Sku})",
            request.DatabaseName, dbResource.Data.Status, dbResource.Data.Sku?.Name);

        return MapToStatusDto(dbResource);
    }

    /// <inheritdoc />
    public async Task<List<AzureDatabaseStatusDto>> ListDatabasesAsync(
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing all databases on Azure SQL Server");

        var sqlDatabases = await GetSqlDatabaseCollectionAsync(cancellationToken);
        var result = new List<AzureDatabaseStatusDto>();

        // Iterate over all databases on the server (including system DBs like master)
        await foreach (var db in sqlDatabases.GetAllAsync(cancellationToken: cancellationToken))
        {
            result.Add(MapToStatusDto(db));
        }

        _logger.LogInformation("Found {Count} databases on Azure SQL Server", result.Count);
        return result;
    }

    /// <inheritdoc />
    public async Task<AzureDatabaseStatusDto?> GetDatabaseStatusAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting status of Azure SQL database '{DatabaseName}'", databaseName);

        var sqlDatabases = await GetSqlDatabaseCollectionAsync(cancellationToken);

        try
        {
            var dbResource = await sqlDatabases.GetAsync(databaseName, cancellationToken: cancellationToken);
            return MapToStatusDto(dbResource.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Database not found — return null instead of throwing
            _logger.LogWarning("Azure SQL database '{DatabaseName}' not found", databaseName);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDatabaseAsync(
        string databaseName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "Deleting Azure SQL database '{DatabaseName}' — this is a DESTRUCTIVE operation",
            databaseName);

        var sqlDatabases = await GetSqlDatabaseCollectionAsync(cancellationToken);

        try
        {
            var dbResource = await sqlDatabases.GetAsync(databaseName, cancellationToken: cancellationToken);
            await dbResource.Value.DeleteAsync(WaitUntil.Completed, cancellationToken);

            _logger.LogInformation("Azure SQL database '{DatabaseName}' deleted successfully", databaseName);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Azure SQL database '{DatabaseName}' not found — nothing to delete", databaseName);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task CreateContainedUserAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        // Read the Azure Function managed identity name from configuration.
        // This is the name of the system-assigned or user-assigned managed identity
        // of the Azure Function App that needs access to each tenant database.
        var identityName = GetRequiredSetting("AzureSettings:FunctionAppIdentityName");

        // SECURITY: Validate the identity name to prevent SQL injection.
        // CREATE USER statements CANNOT use parameterized queries (@parameters),
        // so we must ensure the name only contains safe characters.
        if (!SafeIdentityNameRegex().IsMatch(identityName))
        {
            throw new InvalidOperationException(
                $"FunctionAppIdentityName '{identityName}' contains invalid characters. " +
                "Only alphanumeric characters, hyphens, and underscores are allowed.");
        }

        _logger.LogInformation(
            "Creating contained user '{IdentityName}' in tenant database",
            identityName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Check if the user already exists in this database (idempotent operation).
        // sys.database_principals contains all users, roles, and application roles in the database.
        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM sys.database_principals WHERE name = @name";
        checkCmd.Parameters.AddWithValue("@name", identityName);
        var userExists = (int)(await checkCmd.ExecuteScalarAsync(cancellationToken))! > 0;

        if (!userExists)
        {
            // CREATE USER ... FROM EXTERNAL PROVIDER creates an Azure AD (Entra ID) contained user.
            // This allows the Azure Function to authenticate using its managed identity
            // without any passwords or connection string secrets.
            await using var createCmd = connection.CreateCommand();
            createCmd.CommandText = $"CREATE USER [{identityName}] FROM EXTERNAL PROVIDER";
            await createCmd.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Created contained user '{IdentityName}'", identityName);
        }
        else
        {
            _logger.LogInformation("Contained user '{IdentityName}' already exists — skipping creation", identityName);
        }

        // Grant db_datareader and db_datawriter roles to the managed identity.
        // These built-in roles allow:
        // - db_datareader: SELECT on all user tables and views
        // - db_datawriter: INSERT, UPDATE, DELETE on all user tables
        // This is sufficient for Azure Functions to read/write tenant data but NOT modify schema.
        await using var readerCmd = connection.CreateCommand();
        readerCmd.CommandText = $"ALTER ROLE db_datareader ADD MEMBER [{identityName}]";
        await readerCmd.ExecuteNonQueryAsync(cancellationToken);

        await using var writerCmd = connection.CreateCommand();
        writerCmd.CommandText = $"ALTER ROLE db_datawriter ADD MEMBER [{identityName}]";
        await writerCmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.LogInformation(
            "Granted db_datareader and db_datawriter roles to '{IdentityName}'",
            identityName);
    }

    #region Private helpers

    /// <summary>
    /// Creates an authenticated ArmClient using either service principal credentials
    /// or DefaultAzureCredential (managed identity, Azure CLI, etc.).
    ///
    /// Credential resolution order:
    /// 1. If AzureSettings:TenantId + ClientId + ClientSecret are ALL configured → ClientSecretCredential
    /// 2. Otherwise → DefaultAzureCredential (tries managed identity, VS, Azure CLI, etc.)
    /// </summary>
    private ArmClient CreateArmClient()
    {
        var tenantId = _configuration["AzureSettings:TenantId"];
        var clientId = _configuration["AzureSettings:ClientId"];
        var clientSecret = _configuration["AzureSettings:ClientSecret"];

        TokenCredential credential;

        if (!string.IsNullOrWhiteSpace(tenantId) &&
            !string.IsNullOrWhiteSpace(clientId) &&
            !string.IsNullOrWhiteSpace(clientSecret))
        {
            // Service principal authentication — used when running outside Azure
            // (e.g., local development without Azure CLI, or CI/CD pipelines)
            credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            _logger.LogDebug("Using ClientSecretCredential for Azure ARM authentication");
        }
        else
        {
            // DefaultAzureCredential tries multiple sources in order:
            // 1. EnvironmentCredential (AZURE_* env vars)
            // 2. ManagedIdentityCredential (Azure VM, App Service, Functions)
            // 3. VisualStudioCredential
            // 4. AzureCliCredential (az login)
            // 5. AzurePowerShellCredential
            credential = new DefaultAzureCredential();
            _logger.LogDebug("Using DefaultAzureCredential for Azure ARM authentication");
        }

        return new ArmClient(credential);
    }

    /// <summary>
    /// Navigates the Azure Resource Manager hierarchy to get the SQL databases collection:
    /// Subscription → Resource Group → SQL Server → Databases
    ///
    /// Reads SubscriptionId, ResourceGroupName, and SqlServerName from AzureSettings config.
    /// </summary>
    private async Task<SqlDatabaseCollection> GetSqlDatabaseCollectionAsync(
        CancellationToken cancellationToken)
    {
        var subscriptionId = GetRequiredSetting("AzureSettings:SubscriptionId");
        var resourceGroupName = GetRequiredSetting("AzureSettings:ResourceGroupName");
        var sqlServerName = GetRequiredSetting("AzureSettings:SqlServerName");

        var armClient = CreateArmClient();

        // Navigate the ARM resource hierarchy:
        // SubscriptionResource → ResourceGroup → SqlServer → SqlDatabases
        var subscription = armClient.GetSubscriptionResource(
            new ResourceIdentifier($"/subscriptions/{subscriptionId}"));

        var resourceGroup = await subscription
            .GetResourceGroupAsync(resourceGroupName, cancellationToken);

        var sqlServer = await resourceGroup.Value
            .GetSqlServerAsync(sqlServerName, cancellationToken: cancellationToken);

        return sqlServer.Value.GetSqlDatabases();
    }

    /// <summary>
    /// Maps an Azure SQL database resource to the simplified status DTO.
    /// Extracts only the properties relevant for SysAdmin monitoring.
    /// </summary>
    private static AzureDatabaseStatusDto MapToStatusDto(SqlDatabaseResource dbResource)
    {
        var data = dbResource.Data;

        return new AzureDatabaseStatusDto
        {
            DatabaseName = data.Name,
            Status = data.Status?.ToString() ?? "Unknown",
            CreationDate = data.CreatedOn,
            MaxSizeBytes = data.MaxSizeBytes,
            // Combine tier and SKU name for display (e.g., "GeneralPurpose / GP_S_Gen5")
            Sku = data.Sku != null
                ? $"{data.Sku.Tier} / {data.Sku.Name}"
                : "Unknown",
            UseFreeLimit = data.UseFreeLimit ?? false,
            FreeLimitExhaustionBehavior = data.FreeLimitExhaustionBehavior?.ToString(),
            EarliestRestoreDate = data.EarliestRestoreOn,
            Location = data.Location.DisplayName ?? data.Location.Name ?? string.Empty
        };
    }

    /// <summary>
    /// Reads a required configuration value from appsettings.json.
    /// Throws InvalidOperationException with a clear message if the value is missing.
    /// </summary>
    private string GetRequiredSetting(string key)
    {
        return _configuration[key]
            ?? throw new InvalidOperationException(
                $"Configuration key '{key}' is not set. " +
                "Add it to appsettings.json or set via environment variable " +
                $"(e.g., {key.Replace(":", "__")}).");
    }

    #endregion
}
