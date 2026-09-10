using System.IO.Compression;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the ISDOC export endpoint wiring of received invoices and
/// the bulk ISDOC ZIP endpoints:
///   - ReceivedInvoiceController.ExportIsdoc / BulkExportIsdoc
///   - InvoiceController.BulkExportIsdoc
///   - Azure Functions wrappers (attribute smoke tests)
///
/// Transport layer only — the mapper/service logic is covered in
/// IsdocReceivedInvoiceExportTests.cs.
/// </summary>
public class IsdocReceivedInvoiceEndpointTests
{
    // ── Test doubles ──────────────────────────────────────────────────────────

    private readonly IReceivedInvoiceService _service = Substitute.For<IReceivedInvoiceService>();
    private readonly IIsdocExportService _isdocExportService = Substitute.For<IIsdocExportService>();

    // System under test: the controller
    private readonly ReceivedInvoiceController _controller;

    public IsdocReceivedInvoiceEndpointTests()
    {
        _controller = new ReceivedInvoiceController(
            _service,
            Substitute.For<IPaymentMatchingService>(),
            Substitute.For<IFileAttachmentService>(),
            _isdocExportService,
            Substitute.For<ILogger<ReceivedInvoiceController>>());
    }

    private static ReceivedInvoiceDto MakeDto(long id, string docNumber) => new()
    {
        Id = id,
        DocumentNumber = docNumber,
        Status = EReceivedInvoiceStatus.Received,
        SupplierName = "Dodavatel s.r.o."
    };

    // ── Single export ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportIsdoc_ExistingInvoice_ReturnsFileWithXmlContentType()
    {
        var fakeXmlBytes = "<ISDOC>received</ISDOC>"u8.ToArray();
        _isdocExportService.ExportReceivedInvoiceAsync(42, Arg.Any<CancellationToken>())
            .Returns(fakeXmlBytes);
        _service.GetByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(MakeDto(42, "FV2026001"));

        var result = await _controller.ExportIsdoc(42, CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/xml");
        fileResult.FileContents.ShouldBe(fakeXmlBytes);
        fileResult.FileDownloadName.ShouldBe("ReceivedInvoice_FV2026001.isdoc");
    }

    [Fact]
    public async Task ExportIsdoc_InvoiceNotFound_Returns404()
    {
        _isdocExportService.ExportReceivedInvoiceAsync(999, Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException("Received invoice 999 not found."));

        var result = await _controller.ExportIsdoc(999, CancellationToken.None);

        var notFound = result.ShouldBeOfType<NotFoundObjectResult>();
        notFound.StatusCode.ShouldBe(404);
    }

    // ── Bulk export ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BulkExportIsdoc_TwoInvoices_ReturnsZipWithTwoEntries()
    {
        _service.GetDocumentNumbersAsync(
                Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string?> { [1] = "FV001", [2] = "FV002" });
        _isdocExportService.ExportReceivedInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns("<ISDOC/>"u8.ToArray());

        var result = await _controller.BulkExportIsdoc("1,2", CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/zip");

        using var archive = new ZipArchive(new MemoryStream(fileResult.FileContents), ZipArchiveMode.Read);
        archive.Entries.Count.ShouldBe(2);
        archive.Entries.Select(e => e.Name)
            .ShouldBe(new[] { "ReceivedInvoice_FV001.isdoc", "ReceivedInvoice_FV002.isdoc" }, ignoreOrder: true);
    }

    [Fact]
    public async Task BulkExportIsdoc_FailedInvoiceIsSkipped()
    {
        _service.GetDocumentNumbersAsync(
                Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string?> { [1] = "FV001", [2] = "FV002" });
        _isdocExportService.ExportReceivedInvoiceAsync(1, Arg.Any<CancellationToken>())
            .Returns("<ISDOC/>"u8.ToArray());
        _isdocExportService.ExportReceivedInvoiceAsync(2, Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException("gone"));

        var result = await _controller.BulkExportIsdoc("1,2", CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        using var archive = new ZipArchive(new MemoryStream(fileResult.FileContents), ZipArchiveMode.Read);
        archive.Entries.Count.ShouldBe(1);
        archive.Entries[0].Name.ShouldBe("ReceivedInvoice_FV001.isdoc");
    }

    [Fact]
    public async Task BulkExportIsdoc_DuplicateDocumentNumbers_GetUniqueEntryNames()
    {
        // Supplier document numbers are not unique across suppliers — the ZIP
        // must deduplicate entry names instead of silently overwriting files.
        _service.GetDocumentNumbersAsync(
                Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string?> { [1] = "FV001", [2] = "FV001" });
        _isdocExportService.ExportReceivedInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns("<ISDOC/>"u8.ToArray());

        var result = await _controller.BulkExportIsdoc("1,2", CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        using var archive = new ZipArchive(new MemoryStream(fileResult.FileContents), ZipArchiveMode.Read);
        archive.Entries.Count.ShouldBe(2);
        archive.Entries.Select(e => e.Name).Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task BulkExportIsdoc_NoValidIds_ReturnsBadRequest()
    {
        var result = await _controller.BulkExportIsdoc("abc,,0", CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task BulkExportIsdoc_NothingExportable_Returns404()
    {
        // Ids unknown in this tenant → empty document number lookup → empty ZIP → 404.
        _service.GetDocumentNumbersAsync(
                Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<long, string?>());

        var result = await _controller.BulkExportIsdoc("5,6", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }

    // ── Issued-invoice bulk ISDOC endpoint ────────────────────────────────────

    [Fact]
    public async Task InvoiceController_BulkExportIsdoc_ReturnsZipWithEntries()
    {
        var invoiceService = Substitute.For<IInvoiceService>();
        var isdocService = Substitute.For<IIsdocExportService>();
        var controller = new InvoiceController(
            invoiceService,
            Substitute.For<IPdfExportService>(),
            isdocService,
            Substitute.For<IEmailService>(),
            Substitute.For<IQrPaymentService>(),
            Substitute.For<ICloudStorageOrchestrator>(),
            Substitute.For<IPaymentMatchingService>(),
            Substitute.For<ILogger<InvoiceController>>());

        isdocService.ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns("<ISDOC/>"u8.ToArray());
        invoiceService.GetInvoiceByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Fakvio.Contracts.Dto.Invoice.InvoiceDto { Id = 1, DocumentNumber = "INV001" });
        // Invoice 2 fails — must be skipped, not fail the whole ZIP
        isdocService.ExportInvoiceAsync(2, Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException("gone"));

        var result = await controller.BulkExportIsdoc("1,2", CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/zip");
        using var archive = new ZipArchive(new MemoryStream(fileResult.FileContents), ZipArchiveMode.Read);
        archive.Entries.Count.ShouldBe(1);
        archive.Entries[0].Name.ShouldBe("Invoice_INV001.isdoc");
    }
}
