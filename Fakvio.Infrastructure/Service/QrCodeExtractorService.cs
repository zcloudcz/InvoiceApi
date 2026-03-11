using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using ZXing;
using ZXing.SkiaSharp;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Extracts QR codes from PDF documents by scanning embedded images.
///
/// How it works:
/// 1. Opens the PDF with iText7's PdfReader
/// 2. For each page, uses PdfCanvasProcessor with a custom ImageRenderListener
///    to extract all embedded images (raster images inside the PDF)
/// 3. Each image is converted to an SKBitmap via SkiaSharp
/// 4. ZXing BarcodeReader attempts to decode a QR code from the bitmap
/// 5. If decoded, the raw text is parsed as SIND or SPD format
/// 6. Returns the first successfully parsed QR code
///
/// Limitations:
/// - Only finds QR codes embedded as raster images (most common in invoices)
/// - Does NOT detect QR codes drawn as vector paths (very rare)
/// - Large PDFs with many images may be slow (each image is decoded independently)
/// - Max file size: 10 MB (same as PdfTextExtractorService)
/// </summary>
public class QrCodeExtractorService : IQrCodeExtractor
{
    private readonly ILogger<QrCodeExtractorService> _logger;

    /// <summary>Maximum PDF size (10 MB) to prevent memory exhaustion.</summary>
    private const int MaxPdfSizeBytes = 10 * 1024 * 1024;

    public QrCodeExtractorService(ILogger<QrCodeExtractorService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scans a PDF for QR codes and returns parsed SIND/SPD data.
    /// Never throws — returns QrExtractionResult.NotFound() on any error.
    /// </summary>
    public Task<QrExtractionResult> ExtractFromPdfAsync(byte[] pdfBytes)
    {
        // Guard: null or empty input
        if (pdfBytes == null || pdfBytes.Length == 0)
        {
            _logger.LogDebug("Empty PDF bytes, skipping QR extraction");
            return Task.FromResult(QrExtractionResult.NotFound());
        }

        // Guard: file size limit
        if (pdfBytes.Length > MaxPdfSizeBytes)
        {
            _logger.LogWarning("PDF exceeds max size ({Size} bytes), skipping QR extraction", pdfBytes.Length);
            return Task.FromResult(QrExtractionResult.NotFound());
        }

        try
        {
            var result = ExtractQrFromPdf(pdfBytes);
            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            // Never let QR extraction failure break the import pipeline
            _logger.LogWarning(ex, "QR extraction failed: {Message}", ex.Message);
            return Task.FromResult(QrExtractionResult.NotFound());
        }
    }

    /// <summary>
    /// Core extraction logic — synchronous because iText7 and ZXing are sync APIs.
    /// Scans all pages, all images, returns the first valid QR code.
    /// </summary>
    private QrExtractionResult ExtractQrFromPdf(byte[] pdfBytes)
    {
        using var memoryStream = new MemoryStream(pdfBytes);
        using var pdfReader = new PdfReader(memoryStream);
        using var pdfDocument = new PdfDocument(pdfReader);

        var pageCount = pdfDocument.GetNumberOfPages();
        _logger.LogDebug("Scanning {Pages} pages for QR codes", pageCount);

        // Create the ZXing barcode reader — configured for QR codes only.
        // Using SkiaSharp binding for cross-platform image decoding.
        var barcodeReader = new BarcodeReader
        {
            Options = new ZXing.Common.DecodingOptions
            {
                // Only look for QR codes (skip other barcode types for speed)
                PossibleFormats = [BarcodeFormat.QR_CODE],
                // Try harder to find QR codes (rotations, blurry images)
                TryHarder = true
            }
        };

        // Scan each page for embedded images
        for (int pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            var page = pdfDocument.GetPage(pageNum);

            // ImageRenderListener collects all images on the page
            var listener = new ImageRenderListener();
            var processor = new PdfCanvasProcessor(listener);
            processor.ProcessPageContent(page);

            // Try to decode each extracted image as a QR code
            foreach (var imageBytes in listener.ExtractedImages)
            {
                var qrText = DecodeQrFromImage(barcodeReader, imageBytes);
                if (qrText == null) continue;

                _logger.LogInformation("QR code found on page {Page}: {QrText}",
                    pageNum, qrText.Length > 100 ? qrText[..100] + "..." : qrText);

                // Try to parse as SIND or SPD
                var result = ParseQrText(qrText);
                if (result.Found) return result;
            }
        }

        _logger.LogDebug("No QR codes found in PDF");
        return QrExtractionResult.NotFound();
    }

    /// <summary>
    /// Attempts to decode a QR code from raw image bytes using ZXing + SkiaSharp.
    /// Returns the decoded text, or null if no QR code found.
    /// </summary>
    private string? DecodeQrFromImage(BarcodeReader barcodeReader, byte[] imageBytes)
    {
        try
        {
            // Load image into SkiaSharp bitmap
            using var bitmap = SKBitmap.Decode(imageBytes);
            if (bitmap == null) return null;

            // ZXing SkiaSharp binding can decode directly from SKBitmap
            var result = barcodeReader.Decode(bitmap);
            return result?.Text;
        }
        catch (Exception ex)
        {
            // Some images may be in unsupported formats — skip silently
            _logger.LogDebug("Failed to decode image as QR: {Message}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Parses the raw QR code text as SIND or SPD format.
    /// Tries SIND first (more invoice-specific), then SPD.
    /// </summary>
    internal static QrExtractionResult ParseQrText(string qrText)
    {
        // Try SIND format first: "SID*1.0*..."
        if (SindParser.IsSindString(qrText))
        {
            var sind = SindParser.Parse(qrText);
            if (sind != null)
            {
                return new QrExtractionResult
                {
                    Found = true,
                    QrType = "SIND",
                    RawQrText = qrText,
                    Sind = sind
                };
            }
        }

        // Try SPD format: "SPD*1.0*..."
        if (SpdParser.IsSpdString(qrText))
        {
            var spd = SpdParser.Parse(qrText);
            if (spd != null)
            {
                return new QrExtractionResult
                {
                    Found = true,
                    QrType = "SPD",
                    RawQrText = qrText,
                    Spd = spd,
                    // If SPD contains embedded SIND, extract it
                    Sind = spd.EmbeddedSind
                };
            }
        }

        // QR code found but not in SIND/SPD format (could be a URL or other data)
        return new QrExtractionResult
        {
            Found = true,
            QrType = null,
            RawQrText = qrText
        };
    }
}

/// <summary>
/// iText7 event listener that collects all embedded images from a PDF page.
///
/// When iText7's PdfCanvasProcessor encounters a "Do" operator (draw image),
/// it fires an RENDER_IMAGE event. This listener captures the raw image bytes
/// from each such event.
///
/// Junior note: This is the iText7 equivalent of "find all images on a page."
/// The processor walks through the PDF content stream, and this listener
/// gets called for every image it encounters.
/// </summary>
internal class ImageRenderListener : IEventListener
{
    /// <summary>All extracted image byte arrays from the page.</summary>
    public List<byte[]> ExtractedImages { get; } = new();

    /// <summary>
    /// Called by PdfCanvasProcessor for each PDF content stream event.
    /// We only care about RENDER_IMAGE events (embedded images).
    /// </summary>
    public void EventOccurred(IEventData data, EventType type)
    {
        if (type != EventType.RENDER_IMAGE) return;

        if (data is ImageRenderInfo imageInfo)
        {
            try
            {
                // Get the raw image bytes (PNG, JPEG, etc.)
                var imageObject = imageInfo.GetImage();
                var imageBytes = imageObject?.GetImageBytes();
                if (imageBytes is { Length: > 0 })
                {
                    ExtractedImages.Add(imageBytes);
                }
            }
            catch
            {
                // Some images may fail to extract (e.g., unsupported encoding)
                // Silently skip — we'll try the next image
            }
        }
    }

    /// <summary>
    /// Returns which event types this listener is interested in.
    /// We only need RENDER_IMAGE — all other events are ignored.
    /// </summary>
    public ICollection<EventType> GetSupportedEvents() =>
        new HashSet<EventType> { EventType.RENDER_IMAGE };
}
