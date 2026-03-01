namespace Fakvio.Application.Service;

/// <summary>
/// Service interface for generating PDF documents from invoices.
/// This is used to export invoices to PDF format for download or email attachment.
/// </summary>
public interface IPdfExportService
{
    /// <summary>
    /// Generates a PDF document for the specified invoice using the default template.
    /// Loads the invoice from DB, finds the matching HTML template (or uses default),
    /// replaces placeholders with actual data, and converts to PDF.
    /// </summary>
    /// <param name="invoiceId">The ID of the invoice to export.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Byte array containing the generated PDF document.</returns>
    Task<byte[]> GenerateInvoicePdfAsync(long invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Generates a PDF document for the specified invoice with an optional template override.
    /// When contentTemplateId is provided, uses that specific template instead of the default.
    /// When null, falls back to the default template for the document type.
    /// </summary>
    /// <param name="invoiceId">The ID of the invoice to export.</param>
    /// <param name="contentTemplateId">Optional content template ID — overrides the default PDF template.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Byte array containing the generated PDF document.</returns>
    Task<byte[]> GenerateInvoicePdfAsync(long invoiceId, long? contentTemplateId, CancellationToken ct = default);

    /// <summary>
    /// Generates a PDF document from raw HTML content.
    /// Useful for previewing templates or generating custom documents.
    /// </summary>
    /// <param name="html">The HTML content to convert to PDF.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>Byte array containing the generated PDF document.</returns>
    Task<byte[]> GeneratePdfFromHtmlAsync(string html, CancellationToken ct = default);
}
