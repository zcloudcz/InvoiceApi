namespace Fakvio.Application.Service;

/// <summary>
/// Optional customisation of an invoice email. Passed as one trailing parameter to
/// <see cref="IEmailService.SendInvoiceEmailAsync"/> so existing callers (recurring invoices,
/// chat tool, bulk send) keep compiling and simply get the template defaults.
/// </summary>
/// <param name="Subject">Replaces the template subject when not blank.</param>
/// <param name="Message">Plain-text message prepended to the template body when not blank. It is HTML-encoded before use, never injected as raw HTML.</param>
/// <param name="Attachments">Extra, already validated files appended after PDF / ISDOC / UBL. Null means none.</param>
public sealed record SendInvoiceEmailOptions(
    string? Subject = null,
    string? Message = null,
    IReadOnlyList<OutgoingEmailAttachment>? Attachments = null);

/// <summary>
/// A decoded extra attachment. Content type is intentionally absent: the email layer derives it
/// from the file extension so a client can never choose it.
/// </summary>
public sealed record OutgoingEmailAttachment(string FileName, byte[] Content);
