using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.CompanySettings;

/// <summary>
/// DTO for creating a new CompanySystemSettings record in the master database.
/// This is the first step before provisioning — it registers the tenant configuration
/// (schema name, limits) but does NOT create the actual schema yet.
/// Provisioning is a separate step triggered via POST /api/company/{id}/provision.
/// </summary>
public class CreateCompanySystemSettingsDto
{
    /// <summary>
    /// The company (Client with IsIssuer = true) to create settings for.
    /// Must reference an existing company that doesn't already have settings.
    /// </summary>
    [Required]
    public long CompanyId { get; set; }

    /// <summary>
    /// Schema name for this tenant within the shared database.
    /// Convention: "tenant_{companyId}" — only alphanumeric + underscore allowed.
    /// If not provided, the system generates it automatically from the company ID.
    /// </summary>
    [StringLength(200, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9_]+$",
        ErrorMessage = "Schema name can only contain letters, numbers, and underscores.")]
    public string? SchemaName { get; set; }

    /// <summary>
    /// Maximum number of users allowed for this tenant. Null = unlimited.
    /// </summary>
    [Range(1, 10000)]
    public int? MaxUsers { get; set; }

    /// <summary>
    /// Internal admin notes about this tenant.
    /// </summary>
    [StringLength(2000)]
    public string? AdminNotes { get; set; }

    // ─── Company-Specific SMTP Settings (optional at creation) ────────────────
    // Can be set during company creation or updated later.

    /// <summary>
    /// SMTP server hostname (e.g., "smtp.gmail.com").
    /// </summary>
    [StringLength(500)]
    public string? SmtpHost { get; set; }

    /// <summary>
    /// SMTP port (e.g., 587).
    /// </summary>
    [Range(1, 65535)]
    public int? SmtpPort { get; set; }

    /// <summary>
    /// SMTP authentication username.
    /// </summary>
    [StringLength(256)]
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// SMTP authentication password.
    /// </summary>
    [StringLength(500)]
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// "From" email address (e.g., "invoices@mycompany.com").
    /// </summary>
    [StringLength(256)]
    [EmailAddress]
    public string? SmtpSenderEmail { get; set; }

    /// <summary>
    /// "From" display name (e.g., "My Company Invoices").
    /// </summary>
    [StringLength(200)]
    public string? SmtpSenderName { get; set; }

    /// <summary>
    /// Whether to use SSL/TLS for the SMTP connection.
    /// </summary>
    public bool? SmtpUseSsl { get; set; }
}
