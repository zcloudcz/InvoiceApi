namespace Fakvio.Application.Service;

/// <summary>
/// Service interface for extracting text content from PDF documents.
/// Used by the AI chat feature to allow users to upload PDF files
/// and have the AI analyze their text content.
///
/// Note: This handles text-based (digitally created) PDFs only.
/// Scanned/image-only PDFs will return empty or minimal text
/// because OCR (optical character recognition) is out of scope.
/// </summary>
public interface IPdfTextExtractorService
{
    /// <summary>
    /// Extracts all text content from a PDF document.
    /// Iterates through every page and concatenates the text with page separators.
    /// </summary>
    /// <param name="pdfBytes">The raw byte array of the PDF file.</param>
    /// <param name="ct">Cancellation token for async operation.</param>
    /// <returns>
    /// The extracted text content from all pages, separated by page markers.
    /// Returns an empty string if the PDF contains no extractable text.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when the PDF is corrupt, password-protected, or exceeds the size limit.</exception>
    Task<string> ExtractTextAsync(byte[] pdfBytes, CancellationToken ct = default);
}
