using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Email;

/// <summary>
/// DTO for sending an invoice via email.
/// Contains the recipient's email address and optional customization fields.
/// </summary>
public class SendInvoiceEmailDto
{
    /// <summary>
    /// The email address of the invoice recipient (required).
    /// </summary>
    [Required(ErrorMessage = "Recipient email is required.")]
    [EmailAddress(ErrorMessage = "Invalid email address format.")]
    public string RecipientEmail { get; set; } = string.Empty;

    /// <summary>
    /// Optional custom subject line. If not provided, a default subject
    /// like "Invoice INV2025001" will be used.
    /// </summary>
    [MaxLength(500)]
    public string? Subject { get; set; }

    /// <summary>
    /// Optional custom message shown above the templated email body.
    /// Plain text only: it is HTML-encoded and line breaks are kept.
    /// The PDF and ISDOC are always attached; a UBL XML is added for Slovak or
    /// Peppol-reachable buyers.
    /// </summary>
    [MaxLength(5000)]
    public string? Message { get; set; }

    /// <summary>
    /// Optional extra files (max 5, 10 MB decoded in total, whitelisted extensions) attached after
    /// the generated PDF / ISDOC / UBL. Validated server-side before anything is sent.
    /// </summary>
    public List<EmailAttachmentDto>? Attachments { get; set; }
}
