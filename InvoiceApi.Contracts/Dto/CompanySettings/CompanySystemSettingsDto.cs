namespace InvoiceApi.Contracts.Dto.CompanySettings;

/// <summary>
/// DTO for reading CompanySystemSettings data from the master database.
/// Represents the multi-tenant configuration for a single company (tenant).
/// Used by SysAdmin to view tenant infrastructure status.
/// </summary>
public class CompanySystemSettingsDto
{
    /// <summary>
    /// Unique ID of the settings record.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Foreign key to the Client (company/issuer) this settings record belongs to.
    /// </summary>
    public long CompanyId { get; set; }

    /// <summary>
    /// Company name — populated from the linked Client entity for display convenience.
    /// </summary>
    public string CompanyName { get; set; } = string.Empty;

    /// <summary>
    /// PostgreSQL database name for this tenant (e.g., "invoiceapi_tenant_42").
    /// Set during provisioning and immutable afterward.
    /// </summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Whether the tenant database has been provisioned (created + migrated + seeded).
    /// </summary>
    public bool IsProvisioned { get; set; }

    /// <summary>
    /// Whether the tenant is currently active.
    /// Inactive tenants cannot access tenant-scoped endpoints.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// When was the database provisioned? Null if not yet provisioned.
    /// </summary>
    public DateTime? ProvisionedAt { get; set; }

    /// <summary>
    /// Maximum number of users allowed for this tenant. Null means unlimited.
    /// </summary>
    public int? MaxUsers { get; set; }

    /// <summary>
    /// Internal admin notes about this tenant (not visible to tenant users).
    /// </summary>
    public string? AdminNotes { get; set; }

    /// <summary>
    /// Whether the settings record has a custom connection string override.
    /// We don't expose the actual connection string for security — just whether one exists.
    /// </summary>
    public bool HasCustomConnectionString { get; set; }

    // ─── Company SMTP Settings (read-only view) ──────────────────────────────
    // These are returned so Admin/SysAdmin can see what's configured.
    // Password is NEVER exposed — only a flag indicating whether one is set.

    /// <summary>
    /// SMTP server hostname. When non-empty, company uses its own SMTP.
    /// </summary>
    public string? SmtpHost { get; set; }

    /// <summary>
    /// SMTP port number (e.g., 587).
    /// </summary>
    public int? SmtpPort { get; set; }

    /// <summary>
    /// SMTP authentication username.
    /// </summary>
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// "From" email address for outgoing emails.
    /// </summary>
    public string? SmtpSenderEmail { get; set; }

    /// <summary>
    /// "From" display name for outgoing emails.
    /// </summary>
    public string? SmtpSenderName { get; set; }

    /// <summary>
    /// Whether SSL/TLS is used for SMTP connection.
    /// </summary>
    public bool? SmtpUseSsl { get; set; }

    /// <summary>
    /// Indicates whether an SMTP password has been configured.
    /// The actual password is never exposed in the read DTO — only this flag.
    /// </summary>
    public bool HasSmtpPassword { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
