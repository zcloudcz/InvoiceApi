namespace Fakvio.Contracts.Dto.AzureOperation;

/// <summary>
/// Read-only DTO representing the status and configuration of a tenant schema
/// in the shared PostgreSQL database.
///
/// Returned by the tenant operation endpoints for listing, creating, and checking
/// tenant schemas. Each tenant has an isolated schema within the shared database,
/// replacing the previous Azure SQL per-tenant database approach.
///
/// This DTO provides a unified view of the tenant's schema provisioning state,
/// including whether the schema exists, whether it is active, and which company owns it.
/// </summary>
public class TenantSchemaStatusDto
{
    /// <summary>
    /// Name of the PostgreSQL schema for this tenant (e.g., "tenant_42").
    /// This is used as the search_path when connecting to the tenant's data.
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// The master DB company ID that owns this tenant schema.
    /// References Client (IsIssuer = true) in the master database.
    /// </summary>
    public long CompanyId { get; set; }

    /// <summary>
    /// Display name of the company that owns this tenant schema.
    /// Populated from the master DB Client record for convenience in admin UIs.
    /// </summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the PostgreSQL schema has been created and all required tables
    /// have been migrated successfully. A schema is "provisioned" once the
    /// CREATE SCHEMA + EF Core migration has completed without errors.
    /// </summary>
    public bool IsProvisioned { get; set; }

    /// <summary>
    /// Whether this tenant schema is currently active and accepting connections.
    /// An inactive schema exists but is not served by the application
    /// (e.g., suspended for non-payment, pending deletion, or under maintenance).
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When the tenant schema was first created (UTC).
    /// Recorded during the initial provisioning process.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// When the tenant schema was last migrated or updated (UTC).
    /// Updated each time an EF Core migration is applied to this schema.
    /// Null if no migration has been applied after initial creation.
    /// </summary>
    public DateTimeOffset? LastMigratedAt { get; set; }
}
