namespace InvoiceApi.Contracts.Dto.SystemConfiguration;

/// <summary>
/// DTO for reading system configuration (SMTP + JWT settings).
/// Returned by GET /api/system-configuration.
/// SysAdmin-only — regular users never see these settings.
/// </summary>
public class SystemConfigurationDto
{
    public long Id { get; set; }

    // ─── SMTP Settings ───────────────────────────────────────────────────────
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public string? SmtpUsername { get; set; }
    // NOTE: Password is intentionally included so SysAdmin can see the current value.
    // The API is SysAdmin-only, and there's no public exposure.
    public string? SmtpPassword { get; set; }
    public string SmtpSenderEmail { get; set; } = "";
    public string SmtpSenderName { get; set; } = "InvoiceApi";
    public bool SmtpUseSsl { get; set; } = true;

    // ─── JWT Settings ────────────────────────────────────────────────────────
    public int JwtExpirationHours { get; set; } = 24;
}
