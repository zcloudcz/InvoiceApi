using Fakvio.Application.QrPayment;

namespace Fakvio.Application.Service;

/// <summary>
/// Extracts and decodes QR codes from PDF documents.
/// First level in the 3-tier extraction pipeline: QR → AI → Regex.
///
/// The service:
/// 1. Opens the PDF using iText7
/// 2. Scans each page for embedded images
/// 3. Attempts to decode QR codes from each image using ZXing
/// 4. If a QR code is found, parses it as SIND or SPD format
///
/// Returns the first successfully decoded QR code. Most Czech invoices
/// have exactly one QR code — either QR Faktura (SIND) or QR Platba (SPD).
/// </summary>
public interface IQrCodeExtractor
{
    /// <summary>
    /// Extracts QR code data from a PDF document.
    /// Scans all pages and all embedded images for QR codes.
    ///
    /// Returns a result indicating whether a QR code was found,
    /// and if so, the parsed SIND/SPD data.
    /// Never throws — returns Found=false on any error.
    /// </summary>
    /// <param name="pdfBytes">Raw PDF file bytes.</param>
    /// <returns>Extraction result with parsed QR data (if found).</returns>
    Task<QrExtractionResult> ExtractFromPdfAsync(byte[] pdfBytes);
}

/// <summary>
/// Result of QR code extraction from a PDF document.
/// Contains the parsed SIND/SPD data and metadata about the extraction.
/// </summary>
public class QrExtractionResult
{
    /// <summary>True if at least one QR code was found and successfully decoded.</summary>
    public bool Found { get; set; }

    /// <summary>The type of QR code found: "SIND", "SPD", or null if not found.</summary>
    public string? QrType { get; set; }

    /// <summary>Raw text content of the decoded QR code (before SIND/SPD parsing).</summary>
    public string? RawQrText { get; set; }

    /// <summary>Parsed SIND data (if QR type is SIND or SPD with embedded SIND).</summary>
    public SindData? Sind { get; set; }

    /// <summary>Parsed SPD data (if QR type is SPD).</summary>
    public SpdData? Spd { get; set; }

    /// <summary>
    /// Converts the QR extraction result to the unified InvoiceExtractedData format.
    /// Uses SIND data if available (more invoice-specific), otherwise falls back to SPD.
    /// Returns null if no QR code was found.
    /// </summary>
    public InvoiceExtractedData? ToExtractedData()
    {
        if (!Found) return null;

        // Prefer SIND data (richer invoice fields) over pure SPD (payment-only)
        if (Sind != null) return Sind.ToExtractedData();
        if (Spd != null) return Spd.ToExtractedData();
        return null;
    }

    /// <summary>Creates a "not found" result.</summary>
    public static QrExtractionResult NotFound() => new() { Found = false };
}
