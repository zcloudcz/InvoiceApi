using Fakvio.Contracts.Dto.FileAttachment;

namespace Fakvio.Contracts.Dto.Email;

/// <summary>
/// Everything the "Send invoice by e-mail" dialog needs to prefill itself, in one round trip
/// (GET /api/invoice/{id}/email-defaults). Also used by the MCP tool that lets an AI client
/// see the same options before it sends.
/// </summary>
public class InvoiceEmailDefaultsDto
{
    /// <summary>
    /// The client's e-mail contacts, best first (invoice e-mail before general e-mail, primary before
    /// non-primary). The first entry is the one the server uses when a request has no recipient.
    /// </summary>
    public List<EmailRecipientOptionDto> Recipients { get; set; } = [];

    /// <summary>Effective default: attach the PDF (client → company → system default).</summary>
    public bool AttachPdf { get; set; } = true;

    /// <summary>Effective default: attach the ISDOC XML (client → company → system default).</summary>
    public bool AttachIsdoc { get; set; } = true;

    /// <summary>Effective default: attach the UBL XML (explicit setting, or the automatic rule when none is set).</summary>
    public bool AttachUbl { get; set; }

    /// <summary>True when no level configured UBL explicitly, so <see cref="AttachUbl"/> comes from the automatic rule.</summary>
    public bool UblIsAutomatic { get; set; } = true;

    /// <summary>Would the automatic rule attach the UBL for this buyer (Slovak buyer or explicit Peppol ID)?</summary>
    public bool UblAutoWouldAttach { get; set; }

    /// <summary>Files already stored on the invoice that can be added to the e-mail (default: none selected).</summary>
    public List<FileAttachmentDto> StoredAttachments { get; set; } = [];

    /// <summary>Subject the e-mail template would produce (editable in the dialog); null when it cannot be rendered.</summary>
    public string? DefaultSubject { get; set; }
}

/// <summary>One selectable recipient in the send dialog.</summary>
public class EmailRecipientOptionDto
{
    /// <summary>The e-mail address.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Contact label, e.g. "Accounting department" (may be empty).</summary>
    public string? Label { get; set; }

    /// <summary>The contact is flagged primary on the client.</summary>
    public bool IsPrimary { get; set; }

    /// <summary>The contact has the dedicated "invoice e-mail" type.</summary>
    public bool IsInvoiceEmail { get; set; }
}
