using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.CompanySettings;

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
    /// Schema name for this tenant within the shared database (e.g., "tenant_42").
    /// Set during provisioning and immutable afterward.
    /// </summary>
    public string SchemaName { get; set; } = string.Empty;

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

    // ─── Company AI Settings (read-only view) ──────────────────────────────
    // These are returned so Admin/SysAdmin can see what's configured.
    // API keys are NEVER exposed — only flags indicating whether they are set.

    /// <summary>
    /// Default AI provider for this company (e.g., "Claude", "OpenAI").
    /// Null means "use system default".
    /// </summary>
    public string? AiDefaultProvider { get; set; }

    /// <summary>
    /// Claude model identifier. Null means "use system default model".
    /// </summary>
    public string? AiClaudeModel { get; set; }

    /// <summary>
    /// Indicates whether a Claude API key has been configured for this company.
    /// The actual key is never exposed — only this flag.
    /// </summary>
    public bool HasAiClaudeApiKey { get; set; }

    /// <summary>
    /// OpenAI model identifier. Null means "use system default model".
    /// </summary>
    public string? AiOpenAiModel { get; set; }

    /// <summary>
    /// Indicates whether an OpenAI API key has been configured for this company.
    /// </summary>
    public bool HasAiOpenAiApiKey { get; set; }

    /// <summary>
    /// Gemini model identifier. Null means "use system default model".
    /// </summary>
    public string? AiGeminiModel { get; set; }

    /// <summary>
    /// Indicates whether a Gemini API key has been configured for this company.
    /// </summary>
    public bool HasAiGeminiApiKey { get; set; }

    /// <summary>
    /// Ollama server base URL for this company. Null means "use system default".
    /// </summary>
    public string? AiOllamaBaseUrl { get; set; }

    /// <summary>
    /// Ollama model identifier. Null means "use system default model".
    /// </summary>
    public string? AiOllamaModel { get; set; }

    // ─── EPO Header Settings (read-only view) ─────────────────────────────────

    /// <summary>
    /// Czech Financial Administration tax office code (c_ufo).
    /// Required for EPO DPHDP3 / DPHKH1 submissions.
    /// Null = not yet configured.
    /// </summary>
    public int? EpoTaxOfficeCode { get; set; }

    /// <summary>
    /// Czech Financial Administration territorial branch code (c_pracufo).
    /// Required for EPO DPHDP3 / DPHKH1 submissions.
    /// Null = not yet configured.
    /// </summary>
    public int? EpoTaxOfficeBranchCode { get; set; }

    /// <summary>
    /// Phone number of the person filling in the EPO form.
    /// Null = not yet configured.
    /// </summary>
    public string? EpoContactPhone { get; set; }

    /// <summary>
    /// Email address of the person filling in the EPO form.
    /// Null = not yet configured.
    /// </summary>
    public string? EpoContactEmail { get; set; }

    /// <summary>
    /// Full name of the authorized person for EPO document signing.
    /// Null = not yet configured.
    /// </summary>
    public string? EpoAuthorizedPersonName { get; set; }

    /// <summary>
    /// Default VAT reporting period type (Monthly or Quarterly).
    /// Null = not yet configured.
    /// </summary>
    public EVatPeriodType? EpoDefaultPeriodType { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
