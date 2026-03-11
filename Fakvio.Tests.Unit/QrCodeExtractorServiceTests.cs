using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Service;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="QrCodeExtractorService"/>.
///
/// Testing strategy:
/// - ParseQrText (internal static): Tests the QR text → SIND/SPD parsing logic
///   without needing actual PDF files or QR code images.
/// - ExtractFromPdfAsync: Tests error handling with edge cases (null, empty, oversized).
///
/// Note: Testing actual QR code decoding from PDF images would require test fixture
/// PDFs with embedded QR codes. Those are covered by integration tests.
/// </summary>
public class QrCodeExtractorServiceTests
{
    private readonly QrCodeExtractorService _service;

    public QrCodeExtractorServiceTests()
    {
        var logger = Substitute.For<ILogger<QrCodeExtractorService>>();
        _service = new QrCodeExtractorService(logger);
    }

    // ─── ParseQrText tests ───────────────────────────────────────────────

    [Fact]
    public void ParseQrText_ValidSindString_ReturnsSindResult()
    {
        // Arrange: build a valid SIND string using the builder
        var sindString = new SindBuilder()
            .SetDocumentId("FV2026001")
            .SetIssueDate(new DateTime(2026, 3, 1))
            .SetAmount(5850.00m)
            .SetVariableSymbol("2026001")
            .SetCurrency("CZK")
            .SetIssuerRegistrationNumber("12345678")
            .Build();

        // Act
        var result = QrCodeExtractorService.ParseQrText(sindString);

        // Assert
        result.Found.ShouldBeTrue();
        result.QrType.ShouldBe("SIND");
        result.RawQrText.ShouldBe(sindString);
        result.Sind.ShouldNotBeNull();
        result.Sind.DocumentNumber.ShouldBe("FV2026001");
        result.Sind.Amount.ShouldBe(5850.00m);
        result.Sind.IssuerRegistrationNumber.ShouldBe("12345678");
        result.Spd.ShouldBeNull();
    }

    [Fact]
    public void ParseQrText_ValidSpdString_ReturnsSpdResult()
    {
        // Arrange: build a simple SPD string
        var spdString = SpdIntegrator.BuildSimpleSpdString(
            iban: "CZ5855000000001265098001",
            swift: "RZBCCZPP",
            amount: 5850.00m,
            currencyCode: "CZK",
            dueDate: new DateTime(2026, 3, 15),
            variableSymbol: "2026001",
            message: "FV2026001");

        // Act
        var result = QrCodeExtractorService.ParseQrText(spdString);

        // Assert
        result.Found.ShouldBeTrue();
        result.QrType.ShouldBe("SPD");
        result.Spd.ShouldNotBeNull();
        result.Spd.Amount.ShouldBe(5850.00m);
        result.Spd.IBAN.ShouldBe("CZ5855000000001265098001");
        result.Spd.VariableSymbol.ShouldBe("2026001");
    }

    [Fact]
    public void ParseQrText_SpdWithEmbeddedSind_ExtractsBoth()
    {
        // Arrange: SPD with X-INV containing SIND data
        var sindBuilder = new SindBuilder()
            .SetDocumentId("FV2026001")
            .SetIssueDate(new DateTime(2026, 3, 1))
            .SetAmount(5850.00m)
            .SetAccount("CZ5855000000001265098001", "RZBCCZPP")
            .SetCurrency("CZK")
            .SetDueDate(new DateTime(2026, 3, 15))
            .SetIssuerRegistrationNumber("12345678");

        var spdString = SpdIntegrator.BuildSpdWithInvoice(sindBuilder.GetAttributes());

        // Act
        var result = QrCodeExtractorService.ParseQrText(spdString);

        // Assert: both SPD and embedded SIND should be present
        result.Found.ShouldBeTrue();
        result.QrType.ShouldBe("SPD");
        result.Spd.ShouldNotBeNull();
        result.Sind.ShouldNotBeNull();
        result.Sind.DocumentNumber.ShouldBe("FV2026001");
        result.Sind.IssuerRegistrationNumber.ShouldBe("12345678");
    }

    [Fact]
    public void ParseQrText_UnknownFormat_ReturnsFoundWithNullType()
    {
        // Arrange: QR code with content that is not SIND or SPD (e.g., a URL)
        var qrText = "https://www.fakvio.cz/invoice/12345";

        // Act
        var result = QrCodeExtractorService.ParseQrText(qrText);

        // Assert: Found=true (QR was decoded) but no SIND/SPD type
        result.Found.ShouldBeTrue();
        result.QrType.ShouldBeNull();
        result.RawQrText.ShouldBe(qrText);
        result.Sind.ShouldBeNull();
        result.Spd.ShouldBeNull();
    }

    [Fact]
    public void ParseQrText_InvalidSindCrc_FallsThrough()
    {
        // Arrange: looks like SIND but has invalid CRC
        var qrText = "SID*1.0*AM:1000.00*DD:20260301*ID:FV001*CRC32:00000000";

        // Act
        var result = QrCodeExtractorService.ParseQrText(qrText);

        // Assert: SIND parsing fails (bad CRC), but it's still "found" as raw text
        result.Found.ShouldBeTrue();
        result.QrType.ShouldBeNull(); // Not recognized as valid SIND or SPD
    }

    // ─── ToExtractedData tests ───────────────────────────────────────────

    [Fact]
    public void ToExtractedData_WithSind_ReturnsInvoiceData()
    {
        var sindString = new SindBuilder()
            .SetDocumentId("FV2026001")
            .SetIssueDate(new DateTime(2026, 3, 1))
            .SetAmount(5850.00m)
            .SetIssuerRegistrationNumber("12345678")
            .Build();

        var qrResult = QrCodeExtractorService.ParseQrText(sindString);
        var extracted = qrResult.ToExtractedData();

        extracted.ShouldNotBeNull();
        extracted.DocumentNumber.ShouldBe("FV2026001");
        extracted.TotalAmount.ShouldBe(5850.00m);
        extracted.IssuerRegistrationNumber.ShouldBe("12345678");
        extracted.Source.ShouldBe(EExtractionSource.QrCode);
    }

    [Fact]
    public void ToExtractedData_NotFound_ReturnsNull()
    {
        var result = QrExtractionResult.NotFound();
        result.ToExtractedData().ShouldBeNull();
    }

    // ─── ExtractFromPdfAsync error handling ──────────────────────────────

    [Fact]
    public async Task ExtractFromPdfAsync_NullBytes_ReturnsNotFound()
    {
        var result = await _service.ExtractFromPdfAsync(null!);
        result.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractFromPdfAsync_EmptyBytes_ReturnsNotFound()
    {
        var result = await _service.ExtractFromPdfAsync([]);
        result.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractFromPdfAsync_OversizedFile_ReturnsNotFound()
    {
        // 11 MB — exceeds the 10 MB limit
        var oversized = new byte[11 * 1024 * 1024];
        var result = await _service.ExtractFromPdfAsync(oversized);
        result.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractFromPdfAsync_CorruptPdf_ReturnsNotFound()
    {
        // Random bytes — not a valid PDF
        var corrupt = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var result = await _service.ExtractFromPdfAsync(corrupt);
        result.Found.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtractFromPdfAsync_ValidPdfNoQr_ReturnsNotFound()
    {
        // Create a minimal valid PDF without any images (text only)
        var pdfBytes = CreateMinimalPdfWithoutImages();
        var result = await _service.ExtractFromPdfAsync(pdfBytes);
        result.Found.ShouldBeFalse();
    }

    // ─── Helper: create a minimal PDF for testing ────────────────────────

    /// <summary>
    /// Creates a minimal valid PDF with just text content (no embedded images).
    /// Used to verify that the extractor gracefully handles PDFs without QR codes.
    /// </summary>
    private static byte[] CreateMinimalPdfWithoutImages()
    {
        using var ms = new MemoryStream();
        using var writer = new iText.Kernel.Pdf.PdfWriter(ms);
        writer.SetCloseStream(false);
        using var doc = new iText.Kernel.Pdf.PdfDocument(writer);
        var page = doc.AddNewPage();
        var canvas = new iText.Kernel.Pdf.Canvas.PdfCanvas(page);
        canvas.BeginText()
            .SetFontAndSize(iText.Kernel.Font.PdfFontFactory.CreateFont(), 12)
            .MoveText(72, 720)
            .ShowText("Faktura FV2026001")
            .EndText();
        doc.Close();

        ms.Position = 0;
        return ms.ToArray();
    }
}
