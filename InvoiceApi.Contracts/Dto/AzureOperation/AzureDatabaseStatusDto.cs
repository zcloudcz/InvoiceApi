namespace InvoiceApi.Contracts.Dto.AzureOperation;

/// <summary>
/// Read-only DTO representing the status and configuration of an Azure SQL database.
/// Returned by the AzureOperationController endpoints for listing, creating, and checking databases.
///
/// Maps from Azure.ResourceManager.Sql.SqlDatabaseResource properties to a simplified view
/// that SysAdmin can use to monitor tenant databases in the Azure SQL Server.
/// </summary>
public class AzureDatabaseStatusDto
{
    /// <summary>
    /// Name of the database on the Azure SQL Server (e.g., "invoiceapi_tenant_42").
    /// This is the Initial Catalog in connection strings.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the database (e.g., "Online", "Creating", "Paused", "Resuming").
    /// Azure SQL Serverless databases may show "Paused" when auto-paused due to inactivity.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When the database was created on Azure (UTC).
    /// </summary>
    public DateTimeOffset? CreationDate { get; set; }

    /// <summary>
    /// Maximum allowed size of the database in bytes.
    /// For the free tier, this is typically 32GB (34359738368 bytes).
    /// </summary>
    public long? MaxSizeBytes { get; set; }

    /// <summary>
    /// SKU tier and name combined (e.g., "GeneralPurpose / GP_S_Gen5").
    /// GP_S_Gen5 = General Purpose Serverless Gen5, which is the free tier SKU.
    /// </summary>
    public string Sku { get; set; } = string.Empty;

    /// <summary>
    /// Whether this database is using the Azure SQL free tier offer.
    /// True means 100K vCore seconds/month are included at no cost.
    /// </summary>
    public bool UseFreeLimit { get; set; }

    /// <summary>
    /// Behavior when the free tier monthly limit is exhausted.
    /// "AutoPause" = database pauses until next cycle (no charges).
    /// "BillOverUsage" = database keeps running and you pay for overage.
    /// Null if UseFreeLimit is false.
    /// </summary>
    public string? FreeLimitExhaustionBehavior { get; set; }

    /// <summary>
    /// Earliest point-in-time restore available for this database (UTC).
    /// Azure SQL maintains automatic backups with point-in-time restore capability.
    /// </summary>
    public DateTimeOffset? EarliestRestoreDate { get; set; }

    /// <summary>
    /// Azure region where the database is hosted (e.g., "westeurope", "northeurope").
    /// </summary>
    public string Location { get; set; } = string.Empty;
}
