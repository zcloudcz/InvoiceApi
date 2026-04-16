using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Stores global system configuration: app settings, SMTP, and JWT.
/// This is a single-row table in the master database — there is always exactly one record.
///
/// SysAdmin can edit these settings via the /system-settings Blazor page.
/// EmailService reads SMTP settings from this table (with fallback to appsettings.json).
/// SmtpPassword is encrypted at rest via ICredentialProtector (Data Protection API).
/// </summary>
public class SystemConfiguration : BaseEntity
{
    // ─── Application Settings ─────────────────────────────────────────────────
    // General app-wide settings. Editable by SysAdmin, override appsettings.json values.

    /// <summary>
    /// Public display name of the application (used in emails, QR codes, page titles).
    /// Overrides appsettings.json "AppSettings:Name". Default: "Fakvio".
    /// </summary>
    public string AppName { get; set; } = "Fakvio";

    /// <summary>
    /// Base URL of the Blazor WASM frontend (e.g., "https://app.fakvio.cz").
    /// Used for building links in emails (invitation, password reset, etc.).
    /// Overrides appsettings.json "AppSettings:BlazorBaseUrl".
    /// Must NOT have a trailing slash.
    /// </summary>
    public string BlazorBaseUrl { get; set; } = "";

    // ─── SMTP Settings ───────────────────────────────────────────────────────
    // Used by EmailService (MailKit) for sending invoices, invitations, reminders.

    /// <summary>
    /// SMTP server hostname (e.g., "smtp.gmail.com", "smtp.office365.com").
    /// </summary>
    public string SmtpHost { get; set; } = "";

    /// <summary>
    /// SMTP server port. Common values: 587 (STARTTLS), 465 (SSL), 25 (unencrypted).
    /// Default: 587 — the most common secure port for outgoing email.
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// SMTP authentication username (often the same as the sender email).
    /// Null if SMTP server doesn't require authentication.
    /// </summary>
    public string? SmtpUsername { get; set; }

    /// <summary>
    /// SMTP authentication password.
    /// Null if SMTP server doesn't require authentication.
    /// </summary>
    public string? SmtpPassword { get; set; }

    /// <summary>
    /// Email address used as the "From" field in outgoing emails.
    /// </summary>
    public string SmtpSenderEmail { get; set; } = "";

    /// <summary>
    /// Display name used as the "From" field in outgoing emails (e.g., "Fakvio").
    /// </summary>
    public string SmtpSenderName { get; set; } = "Fakvio";

    /// <summary>
    /// Whether to use SSL/TLS when connecting to the SMTP server.
    /// Default: true — always use encrypted connection in production.
    /// </summary>
    public bool SmtpUseSsl { get; set; } = true;

    // ─── JWT Settings ────────────────────────────────────────────────────────
    // Controls how long JWT tokens remain valid after login.

    /// <summary>
    /// How many hours a JWT token stays valid after being issued.
    /// Default: 24 hours. Shorter = more secure, longer = less re-login friction.
    /// </summary>
    public int JwtExpirationHours { get; set; } = 24;

    // ─── AI Settings ─────────────────────────────────────────────────────────
    // System-wide AI provider configuration. Overrides appsettings.json values.
    // Company-level settings in CompanySystemSettings take priority over these.
    // API keys are encrypted at rest via ICredentialProtector.

    /// <summary>
    /// Default AI provider for the entire system (e.g., "Claude", "OpenAI", "Gemini", "Ollama").
    /// Company-level AiDefaultProvider overrides this.
    /// </summary>
    public string? AiDefaultProvider { get; set; }

    /// <summary>Anthropic Claude API key (encrypted at rest).</summary>
    public string? AiClaudeApiKey { get; set; }

    /// <summary>Claude model identifier (e.g., "claude-sonnet-4-6").</summary>
    public string? AiClaudeModel { get; set; }

    /// <summary>OpenAI API key (encrypted at rest).</summary>
    public string? AiOpenAiApiKey { get; set; }

    /// <summary>OpenAI model identifier (e.g., "gpt-4o").</summary>
    public string? AiOpenAiModel { get; set; }

    /// <summary>Google Gemini API key (encrypted at rest).</summary>
    public string? AiGeminiApiKey { get; set; }

    /// <summary>Gemini model identifier (e.g., "gemini-2.0-flash").</summary>
    public string? AiGeminiModel { get; set; }

    /// <summary>Ollama server base URL (e.g., "http://localhost:11434").</summary>
    public string? AiOllamaBaseUrl { get; set; }

    /// <summary>Ollama model identifier (e.g., "gemma3:12b").</summary>
    public string? AiOllamaModel { get; set; }

    // ─── Azure Blob Storage Settings ────────────────────────────────────────
    // System-wide blob storage configuration for file attachments.
    // Company-level settings in CompanySystemSettings take priority over these.
    // Connection string is encrypted at rest via ICredentialProtector.

    /// <summary>
    /// Azure Blob Storage connection string (encrypted at rest).
    /// Used by AzureBlobFileStorage for tenant file attachments.
    /// Company-level AzureBlobConnectionString overrides this.
    /// Example: "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;EndpointSuffix=core.windows.net"
    /// </summary>
    public string? AzureBlobConnectionString { get; set; }

    /// <summary>
    /// Prefix for blob container names. Legacy field — kept for backwards compatibility
    /// with the original "container per tenant" architecture. The new design uses one
    /// shared container (see <see cref="AzureBlobContainerName"/>) so this is unused
    /// by current code. Will be removed in a future cleanup once any legacy migration
    /// scripts are no longer needed.
    /// </summary>
    public string? AzureBlobContainerPrefix { get; set; }

    /// <summary>
    /// Name of the single shared blob container that holds all tenant attachments.
    /// Default when null/empty: "fakvio-files".
    ///
    /// Architecture: ONE container, with each tenant getting a top-level directory
    /// (named by their CompanyId). Blob paths look like "42/a1b2c3d4-e5f6-...pdf".
    /// Tenant isolation is enforced in <c>FileAttachmentService</c> by always
    /// prefixing the blob path with the current tenant's CompanyId from JWT.
    ///
    /// Constraints (Azure rules):
    /// - lowercase letters, digits, and hyphens only
    /// - 3–63 characters
    /// - starts with a letter or digit
    /// </summary>
    public string? AzureBlobContainerName { get; set; }
}
