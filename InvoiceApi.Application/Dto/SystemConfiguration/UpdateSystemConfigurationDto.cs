using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Application.Dto.SystemConfiguration;

/// <summary>
/// DTO for updating system configuration (SMTP + JWT settings).
/// Sent via PUT /api/system-configuration.
/// All SMTP fields are optional — only non-null fields are updated.
/// </summary>
public class UpdateSystemConfigurationDto
{
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
    public string SmtpSenderName { get; set; } = "InvoiceApi";

    public bool SmtpUseSsl { get; set; } = true;

    // ─── JWT Settings ────────────────────────────────────────────────────────

    [Range(1, 720, ErrorMessage = "JWT expiration must be between 1 and 720 hours (30 days).")]
    public int JwtExpirationHours { get; set; } = 24;
}
