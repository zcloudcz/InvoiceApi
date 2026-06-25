using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// AI-based classifier that determines whether an inbound invoice email
/// represents a received invoice (from supplier) or an issued invoice (our own copy).
/// Uses company context (IČO, company name) to make the determination.
/// </summary>
public interface IInvoiceEmailClassifier
{
    /// <summary>
    /// Classifies the invoice direction based on available content.
    /// </summary>
    /// <param name="emailBody">Email body text (plain or stripped HTML).</param>
    /// <param name="pdfText">Extracted text from PDF attachment (null if no PDF).</param>
    /// <param name="isdocXml">Raw ISDOC XML (null if no ISDOC attachment).</param>
    /// <param name="companyIco">Tenant's IČO (registration number).</param>
    /// <param name="companyName">Tenant's company name.</param>
    /// <param name="companyId">Company ID for AI provider resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<InvoiceClassificationResult> ClassifyAsync(
        string? emailBody,
        string? pdfText,
        string? isdocXml,
        string companyIco,
        string companyName,
        long? companyId,
        CancellationToken ct = default);
}

/// <summary>
/// Result of invoice direction classification.
/// </summary>
public record InvoiceClassificationResult(
    EInvoiceDirection Direction,
    decimal Confidence,
    string? IssuerRegistrationNumber,
    string? IssuerName,
    string? RecipientRegistrationNumber,
    string? RecipientName);
