using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Contracts.Dto.AzureOperation;

/// <summary>
/// Request DTO for creating an Azure SQL database via the Azure Resource Manager API.
/// Used by the AzureOperationController to provision a new tenant database on Azure SQL Server.
///
/// The free tier (GP_S_Gen5) offers 32GB storage and 100K vCore seconds/month — ideal for
/// small tenants. Up to 10 free databases per Azure subscription are allowed.
///
/// When UseFreeOffer is true, the database is created with GeneralPurpose Serverless Gen5 SKU
/// and auto-pause is enabled by default to stay within the free usage limits.
/// </summary>
public class AzureCreateDatabaseRequest
{
    /// <summary>
    /// Name of the tenant database to create on Azure SQL Server.
    /// Must contain only alphanumeric characters and underscores (e.g., "invoiceapi_tenant_42").
    /// This becomes the Initial Catalog in the connection string.
    /// </summary>
    [Required(ErrorMessage = "Database name is required.")]
    [StringLength(128, MinimumLength = 1, ErrorMessage = "Database name must be between 1 and 128 characters.")]
    [RegularExpression(@"^[a-zA-Z0-9_]+$", ErrorMessage = "Database name must contain only letters, numbers, and underscores.")]
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// The master DB company ID to link this Azure SQL database to.
    /// Must reference an existing Client (IsIssuer = true) with CompanySystemSettings.
    /// </summary>
    [Required(ErrorMessage = "Company ID is required.")]
    [Range(1, long.MaxValue, ErrorMessage = "Company ID must be a positive number.")]
    public long CompanyId { get; set; }

    /// <summary>
    /// Whether to use the Azure SQL free tier offer (GP_S_Gen5, 100K vCore seconds/month).
    /// Defaults to true. Set to false for paid tiers with guaranteed performance.
    /// Azure allows up to 10 free databases per subscription.
    /// </summary>
    public bool UseFreeOffer { get; set; } = true;

    /// <summary>
    /// Behavior when the free tier monthly limit (100K vCore seconds) is exhausted.
    /// - "AutoPause": database auto-pauses until the next billing cycle (default, no charges)
    /// - "BillOverUsage": database continues running and you pay for excess usage
    /// Only applies when UseFreeOffer is true.
    /// </summary>
    [RegularExpression(@"^(AutoPause|BillOverUsage)$", ErrorMessage = "Must be 'AutoPause' or 'BillOverUsage'.")]
    public string? FreeLimitExhaustionBehavior { get; set; } = "AutoPause";

    /// <summary>
    /// Maximum size of the database in bytes. Defaults to 32GB (34359738368 bytes),
    /// which is the maximum for the Azure SQL free tier.
    /// For paid tiers, this can be increased up to several TB depending on the SKU.
    /// </summary>
    [Range(1073741824, long.MaxValue, ErrorMessage = "Max size must be at least 1GB (1073741824 bytes).")]
    public long? MaxSizeBytes { get; set; } = 34359738368; // 32 GB
}
