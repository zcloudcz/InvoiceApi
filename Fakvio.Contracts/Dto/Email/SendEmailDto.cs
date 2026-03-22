using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Email;

/// <summary>
/// DTO for sending a custom email from the SysAdmin email page.
/// Uses system SMTP settings (Tier 2 — SystemConfiguration table).
/// </summary>
public class SendEmailDto
{
    /// <summary>Recipient email address.</summary>
    [Required]
    [EmailAddress]
    public string To { get; set; } = string.Empty;

    /// <summary>Email subject line.</summary>
    [Required]
    [StringLength(500)]
    public string Subject { get; set; } = string.Empty;

    /// <summary>HTML body of the email.</summary>
    [Required]
    public string HtmlBody { get; set; } = string.Empty;
}
