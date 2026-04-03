using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.SystemConfiguration;

/// <summary>
/// DTO for updating system configuration (SMTP + JWT settings).
/// Sent via PUT /api/system-configuration.
/// All SMTP fields are optional — only non-null fields are updated.
/// </summary>
/// <summary>
/// DTO for updating system configuration.
/// Sent via PUT /api/system-configuration.
///
/// IMPORTANT: Partial update semantics for sensitive fields:
///   - SmtpPassword: null = keep existing, empty = clear, non-empty = encrypt and update
///   - Other fields: always updated from DTO value (use current values for unchanged fields)
/// </summary>
public class UpdateSystemConfigurationDto
{
    // ─── Application Settings ─────────────────────────────────────────────────

    /// <summary>Application display name. Default: "Fakvio".</summary>
    [StringLength(200)]
    public string AppName { get; set; } = "Fakvio";

    /// <summary>Blazor UI base URL for email links. No trailing slash.</summary>
    [StringLength(500)]
    public string BlazorBaseUrl { get; set; } = "";

    // ─── SMTP Settings ───────────────────────────────────────────────────────

    [StringLength(500)]
    public string SmtpHost { get; set; } = "";

    [Range(1, 65535, ErrorMessage = "Port must be between 1 and 65535.")]
    public int SmtpPort { get; set; } = 587;

    [StringLength(500)]
    public string? SmtpUsername { get; set; }

    [StringLength(500)]
    public string? SmtpPassword { get; set; }

    [StringLength(500)]
    [EmailAddress]
    public string SmtpSenderEmail { get; set; } = "";

    [StringLength(200)]
    public string SmtpSenderName { get; set; } = "Fakvio";

    public bool SmtpUseSsl { get; set; } = true;

    // ─── JWT Settings ────────────────────────────────────────────────────────

    [Range(1, 720, ErrorMessage = "JWT expiration must be between 1 and 720 hours (30 days).")]
    public int JwtExpirationHours { get; set; } = 24;

    // ─── AI Settings ─────────────────────────────────────────────────────────
    // Same partial-update semantics as SMTP: null = keep existing, empty = clear.

    [StringLength(50)]
    public string? AiDefaultProvider { get; set; }

    /// <summary>Claude API key. Null = keep existing, "" = clear.</summary>
    [StringLength(500)]
    public string? AiClaudeApiKey { get; set; }

    [StringLength(100)]
    public string? AiClaudeModel { get; set; }

    /// <summary>OpenAI API key. Null = keep existing, "" = clear.</summary>
    [StringLength(500)]
    public string? AiOpenAiApiKey { get; set; }

    [StringLength(100)]
    public string? AiOpenAiModel { get; set; }

    /// <summary>Gemini API key. Null = keep existing, "" = clear.</summary>
    [StringLength(500)]
    public string? AiGeminiApiKey { get; set; }

    [StringLength(100)]
    public string? AiGeminiModel { get; set; }

    [StringLength(500)]
    public string? AiOllamaBaseUrl { get; set; }

    [StringLength(100)]
    public string? AiOllamaModel { get; set; }
}
