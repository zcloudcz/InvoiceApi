using Fakvio.Contracts.Dto.Import;

namespace Fakvio.Application.Service;

/// <summary>
/// Orchestrates the invoice import process from PDF files.
///
/// The import follows a 2-step workflow:
/// 1. Preview: Extract data from PDFs, validate, resolve clients → show to user
/// 2. Confirm: User reviews/edits the data, then confirms → create invoices
///
/// Data extraction uses a 3-tier waterfall pipeline:
/// Level 1: QR code (SIND/SPD) — highest reliability, structured data
/// Level 2: AI extraction — high reliability, can extract line items
/// Level 3: Regex fallback — lowest reliability, works offline
/// </summary>
public interface IInvoiceImportService
{
    /// <summary>
    /// Extracts and validates invoice data from a single PDF file.
    /// Returns a preview DTO that the user can review and edit before confirming.
    ///
    /// Validation includes:
    /// - Client lookup by IČO (IssuerRegistrationNumber or RecipientRegistrationNumber)
    /// - Issuer/recipient matching against the active company
    /// - Document number duplicate detection
    /// - Required field checks (amount, date, client)
    /// </summary>
    /// <param name="pdfBytes">Raw PDF file bytes.</param>
    /// <param name="fileName">Original file name (for display).</param>
    /// <param name="target">Import target: issued or received invoice.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preview DTO with extracted data and validation results.</returns>
    Task<InvoiceImportPreviewDto> PreviewImportAsync(
        byte[] pdfBytes,
        string fileName,
        EImportTarget target,
        CancellationToken ct = default);

    /// <summary>
    /// Previews the import of a structured invoice document — ISDOC or UBL/Peppol BIS
    /// XML (F1.10, see docs/adr/0002-sk-einvoicing-peppol.md) — routed by file extension
    /// (.isdoc/.isdocx vs .xml). Unlike <see cref="PreviewImportAsync"/>, there is no
    /// QR/AI/regex waterfall here: the document is either a well-formed, recognized
    /// ISDOC/UBL invoice (parsed deterministically, no AI) or it isn't (returns an error
    /// preview — this method never throws for a malformed file).
    /// </summary>
    /// <param name="fileBytes">Raw file bytes.</param>
    /// <param name="fileName">Original file name (used to pick the parser and for display).</param>
    /// <param name="target">Import target: issued or received invoice.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preview DTO with extracted data and validation results.</returns>
    Task<InvoiceImportPreviewDto> PreviewStructuredImportAsync(
        byte[] fileBytes,
        string fileName,
        EImportTarget target,
        CancellationToken ct = default);

    /// <summary>
    /// Confirms the import of one or more invoices after user review.
    /// Creates the invoices (and optionally new clients) in the database.
    /// </summary>
    /// <param name="request">Confirmed import data from the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of import results (one per file: success/failure + IDs).</returns>
    Task<List<ImportResultDto>> ConfirmImportAsync(
        ConfirmInvoiceImportRequest request,
        CancellationToken ct = default);
}
