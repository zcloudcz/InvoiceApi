using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.CompanySettings;

/// <summary>
/// DTO for updating an existing CompanySystemSettings record.
/// Only updatable fields are exposed — schema name and provisioning status
/// cannot be changed after provisioning (they're managed by the provisioning service).
/// In the PostgreSQL multi-schema architecture, the schema is created during provisioning
/// and is immutable afterward.
/// </summary>
public class UpdateCompanySystemSettingsDto
{
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

    // ─── Company-Specific SMTP Settings ───────────────────────────────────────
    // All optional — null/empty means "use system SMTP" (fallback).
    // Set SmtpHost to a non-empty value to enable company-specific SMTP.

    /// <summary>
    /// SMTP server hostname (e.g., "smtp.gmail.com").
    /// Set to empty string to clear and fall back to system SMTP.
    /// </summary>
    [StringLength(500)]
    public string? SmtpHost { get; set; }

    /// <summary>
    /// SMTP port (e.g., 587). Ignored if SmtpHost is empty.
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
    /// Send null to keep the existing password unchanged.
    /// Send empty string to clear the password.
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

    // ─── Company-Specific AI Settings ────────────────────────────────────────
    // All optional — null means "keep existing", empty string means "clear and use system default".

    /// <summary>
    /// Default AI provider for this company (e.g., "Claude", "OpenAI", "Gemini", "Ollama").
    /// Set to empty string to clear and fall back to system default.
    /// </summary>
    [StringLength(50)]
    public string? AiDefaultProvider { get; set; }

    /// <summary>
    /// Claude API key.
    /// Send null to keep existing, empty string to clear.
    /// </summary>
    [StringLength(500)]
    public string? AiClaudeApiKey { get; set; }

    /// <summary>
    /// Claude model identifier (e.g., "claude-sonnet-4-6").
    /// </summary>
    [StringLength(100)]
    public string? AiClaudeModel { get; set; }

    /// <summary>
    /// OpenAI API key.
    /// Send null to keep existing, empty string to clear.
    /// </summary>
    [StringLength(500)]
    public string? AiOpenAiApiKey { get; set; }

    /// <summary>
    /// OpenAI model identifier (e.g., "gpt-4o").
    /// </summary>
    [StringLength(100)]
    public string? AiOpenAiModel { get; set; }

    /// <summary>
    /// Gemini API key.
    /// Send null to keep existing, empty string to clear.
    /// </summary>
    [StringLength(500)]
    public string? AiGeminiApiKey { get; set; }

    /// <summary>
    /// Gemini model identifier (e.g., "gemini-2.0-flash").
    /// </summary>
    [StringLength(100)]
    public string? AiGeminiModel { get; set; }

    /// <summary>
    /// Ollama server base URL (e.g., "http://localhost:11434").
    /// Set to empty string to clear.
    /// </summary>
    [StringLength(500)]
    public string? AiOllamaBaseUrl { get; set; }

    /// <summary>
    /// Ollama model identifier (e.g., "gemma3:12b").
    /// </summary>
    [StringLength(100)]
    public string? AiOllamaModel { get; set; }
}
