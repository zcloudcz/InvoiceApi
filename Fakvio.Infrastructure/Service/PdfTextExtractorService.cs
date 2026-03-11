using Fakvio.Application.Service;
using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Extensions.Logging;
using System.Text;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Extracts text content from PDF documents using iText7's PdfTextExtractor.
/// Used by the AI chat feature so users can upload a PDF and ask the AI questions about it.
///
/// How it works:
/// 1. Validates file size (max 10 MB to prevent memory abuse)
/// 2. Opens the PDF byte array with iText7's PdfReader
/// 3. Iterates through every page and extracts text using SimpleTextExtractionStrategy
/// 4. Concatenates all page texts with page number separators
///
/// Limitations:
/// - Only extracts text from digitally created PDFs (text embedded in PDF objects)
/// - Scanned/image-only PDFs will return empty text (OCR not implemented)
/// - Password-protected PDFs will throw an ArgumentException
/// </summary>
public class PdfTextExtractorService : IPdfTextExtractorService
{
    private readonly ILogger<PdfTextExtractorService> _logger;

    /// <summary>
    /// Maximum allowed PDF file size in bytes (10 MB).
    /// Prevents out-of-memory issues from excessively large uploads.
    /// </summary>
    private const int MaxFileSizeBytes = 10 * 1024 * 1024;

    public PdfTextExtractorService(ILogger<PdfTextExtractorService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> ExtractTextAsync(byte[] pdfBytes, CancellationToken ct = default)
    {
        // Validate input — reject null/empty byte arrays early.
        if (pdfBytes == null || pdfBytes.Length == 0)
        {
            throw new ArgumentException("PDF file is empty or null.", nameof(pdfBytes));
        }

        // Enforce file size limit to prevent memory exhaustion.
        if (pdfBytes.Length > MaxFileSizeBytes)
        {
            throw new ArgumentException(
                $"PDF file exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.",
                nameof(pdfBytes));
        }

        _logger.LogInformation("Extracting text from PDF ({SizeKb} KB)", pdfBytes.Length / 1024);

        try
        {
            // Wrap the byte array in a MemoryStream for iText7's PdfReader.
            using var memoryStream = new MemoryStream(pdfBytes);
            using var pdfReader = new PdfReader(memoryStream);
            using var pdfDocument = new PdfDocument(pdfReader);

            var totalPages = pdfDocument.GetNumberOfPages();
            var textBuilder = new StringBuilder();

            _logger.LogDebug("PDF has {PageCount} pages", totalPages);

            // Extract text from each page using SimpleTextExtractionStrategy.
            // This strategy reads text in the order it appears in the PDF content stream,
            // which works well for most text-based documents.
            for (var pageNumber = 1; pageNumber <= totalPages; pageNumber++)
            {
                ct.ThrowIfCancellationRequested();

                var page = pdfDocument.GetPage(pageNumber);
                var pageText = PdfTextExtractor.GetTextFromPage(page, new SimpleTextExtractionStrategy());

                // Add page separator for multi-page documents (helps AI understand document structure).
                if (pageNumber > 1)
                {
                    textBuilder.AppendLine();
                }

                textBuilder.AppendLine($"--- Page {pageNumber} ---");
                textBuilder.AppendLine(pageText);
            }

            var extractedText = textBuilder.ToString().Trim();

            _logger.LogInformation(
                "Successfully extracted {CharCount} characters from {PageCount} pages",
                extractedText.Length, totalPages);

            return Task.FromResult(extractedText);
        }
        catch (BadPasswordException)
        {
            // The PDF is password-protected — we cannot extract text without the password.
            _logger.LogWarning("Cannot extract text: PDF is password-protected");
            throw new ArgumentException("Cannot extract text from a password-protected PDF.");
        }
        catch (PdfException ex)
        {
            // The file is corrupt or not a valid PDF (covers all iText7 PDF parsing errors).
            _logger.LogWarning(ex, "Cannot extract text: PDF is corrupt or invalid");
            throw new ArgumentException("The file is not a valid PDF or is corrupted.");
        }
        catch (OperationCanceledException)
        {
            throw; // Let cancellation propagate without wrapping.
        }
        catch (Exception ex)
        {
            // Catch any other iText7 internal errors and wrap them for the caller.
            _logger.LogError(ex, "Unexpected error while extracting text from PDF");
            throw new ArgumentException("Failed to extract text from the PDF file.", ex);
        }
    }
}
