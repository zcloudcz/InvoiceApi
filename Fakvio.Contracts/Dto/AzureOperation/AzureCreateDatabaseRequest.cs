using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.AzureOperation;

/// <summary>
/// Request DTO for creating a new tenant schema in the shared PostgreSQL database.
/// Used by the tenant provisioning controller to set up an isolated schema for a new tenant.
///
/// In the PostgreSQL single-database multi-schema architecture, each tenant gets their own
/// schema (e.g., "tenant_42") within a shared database. This replaces the previous approach
/// of creating separate Azure SQL databases per tenant.
///
/// The schema name is derived from the CompanyId and follows PostgreSQL naming conventions:
/// lowercase letters, digits, and underscores only (max 63 characters per PostgreSQL limit).
/// </summary>
public class CreateTenantSchemaRequest
{
    /// <summary>
    /// Name of the PostgreSQL schema to create for the tenant (e.g., "tenant_42").
    /// Must contain only lowercase alphanumeric characters and underscores.
    /// This becomes the search_path for tenant-scoped queries.
    /// PostgreSQL schema names are limited to 63 characters (NAMEDATALEN - 1).
    /// </summary>
    [Required(ErrorMessage = "Schema name is required.")]
    [StringLength(63, MinimumLength = 1, ErrorMessage = "Schema name must be between 1 and 63 characters.")]
    [RegularExpression(@"^[a-z][a-z0-9_]*$", ErrorMessage = "Schema name must start with a lowercase letter and contain only lowercase letters, numbers, and underscores.")]
    public string SchemaName { get; set; } = string.Empty;

    /// <summary>
    /// The master DB company ID to link this tenant schema to.
    /// Must reference an existing Client (IsIssuer = true) with CompanySystemSettings.
    /// Each company gets exactly one tenant schema in the shared database.
    /// </summary>
    [Required(ErrorMessage = "Company ID is required.")]
    [Range(1, long.MaxValue, ErrorMessage = "Company ID must be a positive number.")]
    public long CompanyId { get; set; }
}
