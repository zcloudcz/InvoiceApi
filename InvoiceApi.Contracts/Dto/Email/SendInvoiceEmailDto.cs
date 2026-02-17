using System.ComponentModel.DataAnnotations;

namespace InvoiceApi.Contracts.Dto.Email;

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
    /// Optional custom message to include in the email body.
    /// The PDF invoice will always be attached regardless.
    /// </summary>
    [MaxLength(5000)]
    public string? Message { get; set; }
}
