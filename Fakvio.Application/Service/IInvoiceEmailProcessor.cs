using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Per-tenant processor for inbound invoice emails.
/// Archives the email, extracts invoice data from attachments (PDF/ISDOC/UBL),
/// classifies direction (received vs issued), creates the invoice, and notifies users.
/// </summary>
public interface IInvoiceEmailProcessor
{
    /// <summary>
    /// Processes a single inbound invoice email end-to-end.
    /// </summary>
    /// <param name="payload">Email metadata and body content.</param>
    /// <param name="attachments">PDF, ISDOC and UBL/Peppol attachments extracted from the email.</param>
    /// <param name="companyId">Tenant's company ID (for AI provider resolution).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Final processing status.</returns>
    Task<EInvoiceEmailStatus> ProcessAsync(
        InvoiceEmailPayload payload,
        IReadOnlyList<EmailAttachment> attachments,
        long companyId,
        CancellationToken ct = default);
}

/// <summary>
/// Email metadata and body for invoice processing.
/// </summary>
public record InvoiceEmailPayload(
    long InvoiceMailboxId,
    string MessageId,
    string? ImapUid,
    DateTime ServerReceivedAt,
    string FromAddress,
    string? FromDisplayName,
    string ToAddress,
    string? Subject,
    DateTime? EmailDate,
    string? TextBody,
    string? HtmlBody);

/// <summary>
/// An email attachment (PDF, ISDOC XML, or UBL/Peppol BIS XML).
/// </summary>
public record EmailAttachment(
    string FileName,
    string ContentType,
    byte[] Content);
