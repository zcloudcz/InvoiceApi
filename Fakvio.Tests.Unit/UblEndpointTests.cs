using Fakvio.API.Controller;
using Fakvio.Application.Exceptions;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the UBL export endpoint wiring (ADR 0002, F1.5):
///   - InvoiceController.ExportUbl / BulkExportUbl — HTTP actions
///
/// Mirrors <see cref="IsdocEndpointTests"/> — transport layer only, mapper/service logic is
/// covered separately (<c>UblMapperTests</c>, <c>UblPreflightTests</c>, <c>UblExportServiceTests</c>).
/// </summary>
public class UblEndpointTests
{
    private readonly IInvoiceService _invoiceService = Substitute.For<IInvoiceService>();
    private readonly IUblExportService _ublExportService = Substitute.For<IUblExportService>();
    private readonly InvoiceController _controller;

    public UblEndpointTests()
    {
        _controller = new InvoiceController(
            _invoiceService,
            Substitute.For<IPdfExportService>(),
            Substitute.For<IIsdocExportService>(),
            _ublExportService,
            Substitute.For<IEmailService>(),
            Substitute.For<IQrPaymentService>(),
            Substitute.For<ICloudStorageOrchestrator>(),
            Substitute.For<IPaymentMatchingService>(),
            Substitute.For<ILogger<InvoiceController>>());
    }

    private static InvoiceDto MakeInvoiceDto(long id, string docNumber, EDocumentType type = EDocumentType.Invoice)
        => new()
        {
            Id = id,
            DocumentNumber = docNumber,
            DocumentType = type,
            Status = EInvoiceStatus.Completed,
            ClientName = "Test Client",
            IssuerName = "Test Issuer",
            CurrencyCode = "EUR",
            CurrencySymbol = "€"
        };

    [Fact]
    public async Task ExportUbl_ExistingInvoice_ReturnsFileWithXmlContentType()
    {
        var invoiceId = 42L;
        var docNumber = "INV2026001";
        var fakeXmlBytes = "<Invoice>test</Invoice>"u8.ToArray();

        _ublExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>()).Returns(fakeXmlBytes);
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, docNumber));

        var result = await _controller.ExportUbl(invoiceId, CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/xml");
        fileResult.FileContents.ShouldBe(fakeXmlBytes);
        fileResult.FileDownloadName.ShouldBe($"Invoice_{docNumber}.xml");
    }

    [Fact]
    public async Task ExportUbl_CreditNote_UsesCorrectFileNamePrefix()
    {
        var invoiceId = 99L;
        var docNumber = "CN2026001";

        _ublExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns("<CreditNote/>"u8.ToArray());
        _invoiceService.GetInvoiceByIdAsync(invoiceId, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(invoiceId, docNumber, EDocumentType.CreditNote));

        var result = await _controller.ExportUbl(invoiceId, CancellationToken.None);

        result.ShouldBeOfType<FileContentResult>().FileDownloadName.ShouldBe($"CreditNote_{docNumber}.xml");
    }

    [Fact]
    public async Task ExportUbl_InvoiceNotFound_Returns404()
    {
        var missingId = 9999L;
        _ublExportService.ExportInvoiceAsync(missingId, Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException($"Invoice {missingId} not found."));

        var result = await _controller.ExportUbl(missingId, CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>().StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task ExportUbl_NotReady_Returns400_WithIssues()
    {
        var invoiceId = 7L;
        var issues = new List<ReadinessIssueDto>
        {
            new() { Code = "EINVOICE_DRAFT", Severity = EReadinessSeverity.Blocking }
        };
        _ublExportService.ExportInvoiceAsync(invoiceId, Arg.Any<CancellationToken>())
            .Throws(new TenantNotReadyException(issues));

        var result = await _controller.ExportUbl(invoiceId, CancellationToken.None);

        var badRequest = result.ShouldBeOfType<BadRequestObjectResult>();
        badRequest.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task BulkExportUbl_SkipsFailedInvoices_ReturnsZipWithSuccessfulOnes()
    {
        _ublExportService.ExportInvoiceAsync(1, Arg.Any<CancellationToken>())
            .Returns("<Invoice/>"u8.ToArray());
        _invoiceService.GetInvoiceByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(MakeInvoiceDto(1, "INV2026001"));

        _ublExportService.ExportInvoiceAsync(2, Arg.Any<CancellationToken>())
            .Throws(new TenantNotReadyException([new ReadinessIssueDto { Code = "EINVOICE_DRAFT", Severity = EReadinessSeverity.Blocking }]));

        var result = await _controller.BulkExportUbl("1,2", CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/zip");
    }

    [Fact]
    public async Task BulkExportUbl_AllFail_Returns404()
    {
        _ublExportService.ExportInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new KeyNotFoundException("not found"));

        var result = await _controller.BulkExportUbl("1,2", CancellationToken.None);

        result.ShouldBeOfType<NotFoundObjectResult>();
    }
}
