using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.Email;

/// <summary>
/// DTO for sending an invoice via email.
/// Contains the recipient's email address and optional customization fields.
/// </summary>
public class SendInvoiceEmailDto
{
    /// <summary>
    /// The email address of the invoice recipient. Optional: when blank, the server uses the client's
    /// default e-mail contact (invoice e-mail, else primary e-mail) and answers 400 if the client has none.
    /// </summary>
    [EmailAddress(ErrorMessage = "Invalid email address format.")]
    public string? RecipientEmail { get; set; }

    /// <summary>
    /// Optional CC recipients (max 10, bare addresses like "jan@firma.cz"). Every address is validated
    /// server-side; one bad address rejects the whole request and nothing is sent.
    /// </summary>
    public List<string>? CcEmails { get; set; }

    /// <summary>
    /// Optional custom subject line. If not provided, a default subject
    /// like "Invoice INV2025001" will be used.
    /// </summary>
    [MaxLength(500)]
    public string? Subject { get; set; }

    /// <summary>
    /// Optional custom message shown above the templated email body.
    /// Plain text only: it is HTML-encoded and line breaks are kept.
    /// </summary>
    [MaxLength(5000)]
    public string? Message { get; set; }

    /// <summary>
    /// Attach the generated PDF? Null = use the client's default (client → company → system default: yes).
    /// </summary>
    public bool? AttachPdf { get; set; }

    /// <summary>
    /// Attach the generated ISDOC XML? Null = use the client's default (client → company → system default: yes).
    /// </summary>
    public bool? AttachIsdoc { get; set; }

    /// <summary>
    /// Attach the generated UBL / Peppol XML? Null = use the client's default (automatic = only for Slovak
    /// or Peppol-reachable buyers). True is honoured for ANY buyer and fails the request (400) if the UBL
    /// cannot be generated, so the sender never believes it was attached when it was not.
    /// </summary>
    public bool? AttachUbl { get; set; }

    /// <summary>
    /// Ids of files already stored on THIS invoice (Invoice attachments) to include in the e-mail.
    /// Every id must belong to this invoice, otherwise the request is rejected with 400.
    /// </summary>
    public List<long>? FileAttachmentIds { get; set; }

    /// <summary>
    /// When true, the chosen <see cref="AttachPdf"/> / <see cref="AttachIsdoc"/> / <see cref="AttachUbl"/> are
    /// stored as the client's new defaults (after a successful send). Fields sent as null are left alone.
    /// </summary>
    public bool SaveAttachmentDefaults { get; set; }

    /// <summary>
    /// Optional extra files (max 5, 10 MB decoded in total, whitelisted extensions) attached after
    /// the generated PDF / ISDOC / UBL. Validated server-side before anything is sent.
    /// </summary>
    public List<EmailAttachmentDto>? Attachments { get; set; }
}
