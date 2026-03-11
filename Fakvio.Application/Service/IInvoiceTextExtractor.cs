using Fakvio.Application.QrPayment;

namespace Fakvio.Application.Service;

/// <summary>
/// Extracts structured invoice data from plain text using regex patterns.
/// This is the third (last) level in the extraction pipeline: QR → AI → Regex.
///
/// Regex extraction is the least reliable method but works offline
/// and doesn't require any AI provider configuration. It serves as the
/// final fallback when QR codes are absent and AI is not available.
///
/// Limitations:
/// - Cannot extract line items (table structures vary too much)
/// - Regex patterns are tuned for Czech invoices (may miss non-standard formats)
/// - Cannot distinguish context (e.g., two IČO numbers — which is issuer, which is recipient?)
/// </summary>
public interface IInvoiceTextExtractor
{
    /// <summary>
    /// Extracts invoice data from plain text using regex patterns.
    /// Never returns null — always returns an InvoiceExtractedData
    /// (with potentially all-null fields if nothing was found).
    /// </summary>
    /// <param name="text">Plain text extracted from a PDF document.</param>
    /// <returns>Extracted data with Source = RegexFallback.</returns>
    InvoiceExtractedData Extract(string text);
}
