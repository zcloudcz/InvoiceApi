using Fakvio.Infrastructure.Service;
using iText.Kernel.Pdf;
using iText.Layout;
using iText.Layout.Element;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for PdfTextExtractorService.
/// Tests text extraction from valid PDFs, empty PDFs, corrupt data, and file size limits.
///
/// Uses iText7 to generate test PDF byte arrays in-memory so tests are self-contained
/// (no external test files needed).
/// </summary>
public class PdfTextExtractorServiceTests
{
    private readonly PdfTextExtractorService _service;
    private readonly ILogger<PdfTextExtractorService> _logger;

    public PdfTextExtractorServiceTests()
    {
        _logger = Substitute.For<ILogger<PdfTextExtractorService>>();
        _service = new PdfTextExtractorService(_logger);
    }

    /// <summary>
    /// Helper: creates a valid PDF byte array with the given text content.
    /// Uses iText7 to build a real PDF document in memory.
    /// </summary>
    private static byte[] CreatePdfWithText(string text)
    {
        using var memoryStream = new MemoryStream();
        using var writer = new PdfWriter(memoryStream);
        writer.SetCloseStream(false); // Keep stream open so we can read bytes.

        using var pdfDoc = new PdfDocument(writer);
        using var document = new Document(pdfDoc);

        document.Add(new Paragraph(text));
        document.Close();

        return memoryStream.ToArray();
    }

    /// <summary>
    /// Helper: creates a valid PDF with multiple pages.
    /// </summary>
    private static byte[] CreateMultiPagePdf(params string[] pageTexts)
    {
        using var memoryStream = new MemoryStream();
        using var writer = new PdfWriter(memoryStream);
        writer.SetCloseStream(false);

        using var pdfDoc = new PdfDocument(writer);
        using var document = new Document(pdfDoc);

        for (var i = 0; i < pageTexts.Length; i++)
        {
            if (i > 0)
            {
                document.Add(new AreaBreak()); // Force new page.
            }

            document.Add(new Paragraph(pageTexts[i]));
        }

        document.Close();
        return memoryStream.ToArray();
    }

    // ─── Valid PDF Tests ──────────────────────────────────────────────────

    [Fact]
    public async Task ExtractText_ValidPdf_ReturnsExtractedText()
    {
        // Arrange — create a PDF with known text content.
        var pdfBytes = CreatePdfWithText("Hello, this is a test document.");

        // Act
        var result = await _service.ExtractTextAsync(pdfBytes);

        // Assert — the extracted text should contain our content.
        result.ShouldNotBeNullOrEmpty();
        result.ShouldContain("Hello, this is a test document.");
    }

    [Fact]
    public async Task ExtractText_MultiPagePdf_ReturnsAllPages()
    {
        // Arrange — create a multi-page PDF.
        var pdfBytes = CreateMultiPagePdf("Page one content.", "Page two content.");

        // Act
        var result = await _service.ExtractTextAsync(pdfBytes);

        // Assert — text from both pages should be present.
        result.ShouldContain("Page one content.");
        result.ShouldContain("Page two content.");
        result.ShouldContain("--- Page 1 ---");
        result.ShouldContain("--- Page 2 ---");
    }

    [Fact]
    public async Task ExtractText_EmptyPdf_ReturnsPageMarkerOnly()
    {
        // Arrange — create a PDF with no text (just an empty page).
        using var memoryStream = new MemoryStream();
        using var writer = new PdfWriter(memoryStream);
        writer.SetCloseStream(false);

        using var pdfDoc = new PdfDocument(writer);
        pdfDoc.AddNewPage(); // Add an empty page.
        pdfDoc.Close();

        var pdfBytes = memoryStream.ToArray();

        // Act
        var result = await _service.ExtractTextAsync(pdfBytes);

        // Assert — should contain the page marker but minimal/no text content.
        result.ShouldContain("--- Page 1 ---");
    }

    // ─── Error Handling Tests ─────────────────────────────────────────────

    [Fact]
    public async Task ExtractText_NullBytes_ThrowsArgumentException()
    {
        // Act & Assert
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _service.ExtractTextAsync(null!));

        ex.Message.ShouldContain("empty or null");
    }

    [Fact]
    public async Task ExtractText_EmptyBytes_ThrowsArgumentException()
    {
        // Act & Assert
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _service.ExtractTextAsync(Array.Empty<byte>()));

        ex.Message.ShouldContain("empty or null");
    }

    [Fact]
    public async Task ExtractText_CorruptBytes_ThrowsArgumentException()
    {
        // Arrange — random bytes that are not a valid PDF.
        var corruptBytes = new byte[] { 0x01, 0x02, 0x03, 0xFF, 0xFE };

        // Act & Assert — should wrap iText7's exception in an ArgumentException.
        await Should.ThrowAsync<ArgumentException>(
            () => _service.ExtractTextAsync(corruptBytes));
    }

    [Fact]
    public async Task ExtractText_OversizedFile_ThrowsArgumentException()
    {
        // Arrange — create a byte array exceeding 10 MB.
        var oversizedBytes = new byte[11 * 1024 * 1024];

        // Act & Assert
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _service.ExtractTextAsync(oversizedBytes));

        ex.Message.ShouldContain("maximum allowed size");
    }

    [Fact]
    public async Task ExtractText_CancellationRequested_ThrowsOperationCancelled()
    {
        // Arrange
        var pdfBytes = CreatePdfWithText("Test");
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately.

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.ExtractTextAsync(pdfBytes, cts.Token));
    }
}
