using Fakvio.Application.QrPayment;

namespace Fakvio.Application.Service;

/// <summary>
/// Extracts structured invoice data from PDF text using an AI provider.
/// This is the second level in the extraction pipeline (after QR code, before regex).
///
/// The AI approach is significantly more reliable than regex because:
/// - AI understands context (e.g., "Datum vystavení" vs "Datum splatnosti")
/// - AI handles varied invoice formats from different accounting software
/// - AI can extract line items (impossible with simple regex)
/// - AI normalizes dates, amounts, and identifiers automatically
///
/// The service is a thin wrapper around IAiProvider — it crafts a specialized
/// system prompt, sends the PDF text, and parses the structured JSON response.
///
/// Returns null on any failure (provider error, JSON parse error, timeout)
/// so the import orchestrator can gracefully fall back to regex extraction.
/// </summary>
public interface IInvoiceAiExtractor
{
    /// <summary>
    /// Returns true if at least one AI provider is configured and available.
    /// The import orchestrator checks this before attempting AI extraction
    /// to avoid unnecessary error handling when no AI is set up.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Sends the extracted PDF text to an AI provider and returns structured invoice data.
    ///
    /// The AI receives a detailed system prompt with the exact JSON schema to produce,
    /// and the PDF text as the user message. It returns a JSON object that is parsed
    /// into <see cref="InvoiceExtractedData"/>.
    ///
    /// Returns null if:
    /// - No AI provider is available
    /// - The AI response is not valid JSON
    /// - The AI provider throws an exception (timeout, rate limit, etc.)
    /// - The cancellation token is triggered
    /// </summary>
    /// <param name="pdfText">Plain text extracted from the PDF document.</param>
    /// <param name="ct">Cancellation token — respects user-initiated cancellation.</param>
    /// <returns>Extracted invoice data, or null if extraction fails.</returns>
    Task<InvoiceExtractedData?> ExtractAsync(string pdfText, CancellationToken ct = default);
}
