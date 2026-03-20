using Fakvio.Application.QrPayment;

namespace Fakvio.Application.Service;

/// <summary>
/// Context information about the importing company, passed to the AI extractor
/// so it can distinguish between issued and received invoices.
///
/// Junior note: If our company IČO appears as the ISSUER on the PDF, it's an issued invoice.
/// If our IČO appears as the RECIPIENT, it's a received invoice.
/// Without this context, the AI can't determine the document direction.
/// </summary>
public record ImportCompanyContext(
    string CompanyName,
    string RegistrationNumber,
    string? TaxNumber,
    bool IsIssuedImport);

/// <summary>
/// Extracts structured invoice data from PDF text using an AI provider.
/// This is the second level in the extraction pipeline (after QR code, before regex).
///
/// CompanyId and company context are passed explicitly so the service can resolve
/// company-specific AI providers and give the AI knowledge about the importing company.
///
/// Returns null on any failure (provider error, JSON parse error, timeout)
/// so the import orchestrator can gracefully fall back to regex extraction.
/// </summary>
public interface IInvoiceAiExtractor
{
    /// <summary>
    /// Sends the extracted PDF text to an AI provider and returns structured invoice data.
    /// </summary>
    /// <param name="companyId">Company ID for resolving company-specific AI provider.</param>
    /// <param name="pdfText">Plain text extracted from the PDF document.</param>
    /// <param name="companyContext">
    /// Company info (name, IČO, DIČ) + import direction (issued/received).
    /// Null if company info is not available (fallback: AI guesses from context).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Extracted invoice data, or null if extraction fails.</returns>
    Task<InvoiceExtractedData?> ExtractAsync(
        long? companyId,
        string pdfText,
        ImportCompanyContext? companyContext = null,
        CancellationToken ct = default);
}
