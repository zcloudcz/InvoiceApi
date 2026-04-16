namespace Fakvio.Contracts.Dto.SystemConfiguration;

/// <summary>
/// DTO for reading system configuration (SMTP + JWT settings).
/// Returned by GET /api/system-configuration.
/// SysAdmin-only — regular users never see these settings.
/// </summary>
public class SystemConfigurationDto
{
    public long Id { get; set; }

    // ─── Application Settings ─────────────────────────────────────────────────
    /// <summary>Application display name (emails, page titles). Default: "Fakvio".</summary>
    public string AppName { get; set; } = "Fakvio";

    /// <summary>Blazor UI base URL for email links (e.g., "https://app.fakvio.cz").</summary>
    public string BlazorBaseUrl { get; set; } = "";

    // ─── SMTP Settings ───────────────────────────────────────────────────────
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    /// <summary>
    /// SMTP password is NEVER sent to the UI — only this boolean flag.
    /// This prevents the password from being re-sent on every save,
    /// which caused unwanted re-encryption or accidental clearing.
    /// The actual password stays encrypted in the database.
    /// </summary>
    public bool HasSmtpPassword { get; set; }
    public string SmtpSenderEmail { get; set; } = "";
    public string SmtpSenderName { get; set; } = "Fakvio";
    public bool SmtpUseSsl { get; set; } = true;

    // ─── JWT Settings ────────────────────────────────────────────────────────
    public int JwtExpirationHours { get; set; } = 24;

    // ─── AI Settings ─────────────────────────────────────────────────────────
    // API keys are NEVER sent to the UI — only boolean HasXxx flags (same pattern as SMTP).
    public string? AiDefaultProvider { get; set; }
    public string? AiClaudeModel { get; set; }
    public bool HasAiClaudeApiKey { get; set; }
    public string? AiOpenAiModel { get; set; }
    public bool HasAiOpenAiApiKey { get; set; }
    public string? AiGeminiModel { get; set; }
    public bool HasAiGeminiApiKey { get; set; }
    public string? AiOllamaBaseUrl { get; set; }
    public string? AiOllamaModel { get; set; }

    // ─── Azure Blob Storage Settings ────────────────────────────────────────
    /// <summary>Whether a blob storage connection string is configured (never expose the actual value).</summary>
    public bool HasAzureBlobConnectionString { get; set; }
    /// <summary>Legacy field — kept for backwards compatibility.</summary>
    public string? AzureBlobContainerPrefix { get; set; }
    /// <summary>Shared blob container name. Null/empty means "use default fakvio-files".</summary>
    public string? AzureBlobContainerName { get; set; }
}
