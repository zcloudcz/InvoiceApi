namespace Fakvio.Application.Service;

/// <summary>
/// Optional customisation of an invoice email. Passed as one trailing parameter to
/// <see cref="IEmailService.SendInvoiceEmailAsync"/> so existing callers (recurring invoices,
/// chat tool, bulk send) keep compiling and simply get the template defaults and the client's
/// default attachments.
/// </summary>
/// <param name="Subject">Replaces the template subject when not blank.</param>
/// <param name="Message">Plain-text message prepended to the template body when not blank. It is HTML-encoded before use, never injected as raw HTML.</param>
/// <param name="Attachments">Extra, already validated files appended after PDF / ISDOC / UBL. Null means none.</param>
/// <param name="CcEmails">CC recipients (already validated by the caller, see <c>EmailAddressList</c>). Null means none.</param>
/// <param name="AttachPdf">Attach the PDF? Null = the client's default (client → company → system default: yes).</param>
/// <param name="AttachIsdoc">Attach the ISDOC? Null = the client's default (system default: yes).</param>
/// <param name="AttachUbl">Attach the UBL? Null = the client's default (system default: automatic - Slovak / Peppol buyers only).
/// True is an explicit user choice: it works for any buyer and a UBL failure fails the send.</param>
/// <param name="FileAttachmentIds">Ids of files stored on this invoice to include. Null means none.</param>
/// <param name="SaveAttachmentDefaults">After a successful send, store the explicit Attach* choices as the client's defaults.</param>
public sealed record SendInvoiceEmailOptions(
    string? Subject = null,
    string? Message = null,
    IReadOnlyList<OutgoingEmailAttachment>? Attachments = null,
    IReadOnlyList<string>? CcEmails = null,
    bool? AttachPdf = null,
    bool? AttachIsdoc = null,
    bool? AttachUbl = null,
    IReadOnlyList<long>? FileAttachmentIds = null,
    bool SaveAttachmentDefaults = false);

/// <summary>
/// A decoded extra attachment. Content type is intentionally absent: the email layer derives it
/// from the file extension so a client can never choose it.
/// </summary>
public sealed record OutgoingEmailAttachment(string FileName, byte[] Content);
