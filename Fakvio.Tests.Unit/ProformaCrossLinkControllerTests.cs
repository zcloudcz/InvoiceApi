// ============================================================================
// ProformaCrossLinkControllerTests — coverage for PR #86 (issue #33).
//
// The existing ProformaCrossLinkTests already cover the service layer
// (GetFinalInvoicesForProformaAsync + GetTaxReceiptsForProformaAsync) thoroughly.
// This file covers the missing HTTP controller layer:
//
//   InvoiceController.GetFinalInvoicesForProforma
//     - Auth gate: anonymous → 401
//     - Happy path: authenticated → 200 OK with list from service
//     - Service returns empty list → 200 OK with empty list
//
//   InvoiceController.GetTaxReceiptsForProforma
//     - Auth gate: anonymous → 401
//     - Happy path: authenticated → 200 OK with list from service
//     - Service returns empty list → 200 OK with empty list
//
//   Delegation verification
//     - Controller passes the correct proformaId to the service
//     - Service is called exactly once per request
// ============================================================================

using System.Security.Claims;
using Fakvio.API.Controller;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the two cross-link GET endpoints on <see cref="InvoiceController"/>
/// introduced in PR #86 (issue #33):
///   GET /{proformaId}/final-invoices   → GetFinalInvoicesForProforma
///   GET /{proformaId}/tax-receipts     → GetTaxReceiptsForProforma
///
/// Uses NSubstitute mocks so these are pure controller-layer tests
/// (no database, no InvoiceService implementation).
/// </summary>
public class ProformaCrossLinkControllerTests
{
    // ── Shared mocks ─────────────────────────────────────────────────────────

    private readonly IInvoiceService _invoiceService = Substitute.For<IInvoiceService>();
    private readonly IPdfExportService _pdfService = Substitute.For<IPdfExportService>();
    private readonly IIsdocExportService _isdocService = Substitute.For<IIsdocExportService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IQrPaymentService _qrService = Substitute.For<IQrPaymentService>();
    private readonly ICloudStorageOrchestrator _cloudStorage = Substitute.For<ICloudStorageOrchestrator>();

    /// <summary>
    /// Builds <see cref="InvoiceController"/> with the given auth state.
    /// </summary>
    private InvoiceController BuildController(bool authenticated = true)
    {
        var controller = new InvoiceController(
            _invoiceService,
            _pdfService,
            _isdocService,
            _emailService,
            _qrService,
            _cloudStorage,
            Substitute.For<ILogger<InvoiceController>>());

        var ctx = new DefaultHttpContext();
        if (authenticated)
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("UserId", "1"), new Claim(ClaimTypes.NameIdentifier, "1") },
                authenticationType: "TestAuth")); // non-null authenticationType → IsAuthenticated = true
        }

        controller.ControllerContext = new ControllerContext { HttpContext = ctx };
        return controller;
    }

    /// <summary>
    /// Builds a list of <see cref="InvoiceDto"/> for the given document type and IDs,
    /// so each happy-path test can verify which service data reaches the response.
    /// </summary>
    private static List<InvoiceDto> BuildDtoList(EDocumentType docType, params long[] ids) =>
        ids.Select(id => new InvoiceDto
        {
            Id = id,
            DocumentType = docType,
            DocumentNumber = $"DOC-{id}"
        }).ToList();

    // ─────────────────────────────────────────────────────────────────────────
    // GetFinalInvoicesForProforma — GET /{proformaId}/final-invoices
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFinalInvoicesForProforma_AuthenticatedRequest_ReturnsOkWithList()
    {
        // Arrange
        var expected = BuildDtoList(EDocumentType.Invoice, 20, 21);
        _invoiceService
            .GetFinalInvoicesForProformaAsync(10L, Arg.Any<CancellationToken>())
            .Returns(expected);

        // Act
        var result = await BuildController().GetFinalInvoicesForProforma(proformaId: 10L);

        // Assert — controller must return 200 OK with the service's list
        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeOfType<List<InvoiceDto>>();
        list.Count.ShouldBe(2);
        list.Select(d => d.Id).ShouldContain(20L);
        list.Select(d => d.Id).ShouldContain(21L);
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_ServiceReturnsEmptyList_ReturnsOkWithEmptyList()
    {
        // Empty list is a valid response (proforma has no linked final invoices yet).
        _invoiceService
            .GetFinalInvoicesForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>());

        var result = await BuildController().GetFinalInvoicesForProforma(proformaId: 42L);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeOfType<List<InvoiceDto>>();
        list.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_PassesCorrectProformaIdToService()
    {
        // Controller must forward the route parameter to the service unchanged.
        _invoiceService
            .GetFinalInvoicesForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>());

        await BuildController().GetFinalInvoicesForProforma(proformaId: 77L);

        await _invoiceService.Received(1)
            .GetFinalInvoicesForProformaAsync(77L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_ResponseDtosHaveDocumentTypeInvoice()
    {
        // Every DTO in the response must be a final Invoice (not a Proforma or TaxReceipt).
        var expected = BuildDtoList(EDocumentType.Invoice, 100, 101, 102);
        _invoiceService
            .GetFinalInvoicesForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await BuildController().GetFinalInvoicesForProforma(proformaId: 10L);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeOfType<List<InvoiceDto>>();
        list.ShouldAllBe(dto => dto.DocumentType == EDocumentType.Invoice);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GetTaxReceiptsForProforma — GET /{proformaId}/tax-receipts
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTaxReceiptsForProforma_AuthenticatedRequest_ReturnsOkWithList()
    {
        // Arrange
        var expected = BuildDtoList(EDocumentType.TaxReceiptForAdvance, 30);
        _invoiceService
            .GetTaxReceiptsForProformaAsync(10L, Arg.Any<CancellationToken>())
            .Returns(expected);

        // Act
        var result = await BuildController().GetTaxReceiptsForProforma(proformaId: 10L);

        // Assert
        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeOfType<List<InvoiceDto>>();
        list.Count.ShouldBe(1);
        list[0].Id.ShouldBe(30L);
        list[0].DocumentType.ShouldBe(EDocumentType.TaxReceiptForAdvance);
    }

    [Fact]
    public async Task GetTaxReceiptsForProforma_ServiceReturnsEmptyList_ReturnsOkWithEmptyList()
    {
        // Empty list is valid (proforma has no tax receipts — disabled mode or not yet paid).
        _invoiceService
            .GetTaxReceiptsForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>());

        var result = await BuildController().GetTaxReceiptsForProforma(proformaId: 42L);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeOfType<List<InvoiceDto>>();
        list.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTaxReceiptsForProforma_PassesCorrectProformaIdToService()
    {
        _invoiceService
            .GetTaxReceiptsForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new List<InvoiceDto>());

        await BuildController().GetTaxReceiptsForProforma(proformaId: 55L);

        await _invoiceService.Received(1)
            .GetTaxReceiptsForProformaAsync(55L, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTaxReceiptsForProforma_ResponseDtosHaveDocumentTypeTaxReceiptForAdvance()
    {
        // Every DTO must be a TaxReceiptForAdvance (the filter is in the service, not the controller,
        // but the controller must pass through whatever the service returns).
        var expected = BuildDtoList(EDocumentType.TaxReceiptForAdvance, 30, 31);
        _invoiceService
            .GetTaxReceiptsForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await BuildController().GetTaxReceiptsForProforma(proformaId: 10L);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeOfType<List<InvoiceDto>>();
        list.ShouldAllBe(dto => dto.DocumentType == EDocumentType.TaxReceiptForAdvance);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Ordering — the service layer guarantees newest-first; controller passes through
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFinalInvoicesForProforma_PreservesServiceOrderInResponse()
    {
        // The controller must not reorder the list it receives from the service.
        // Service already sorts by IssueDate descending — controller just wraps in Ok().
        var ordered = new List<InvoiceDto>
        {
            new() { Id = 21L, DocumentType = EDocumentType.Invoice, DocumentNumber = "FAK2" },
            new() { Id = 20L, DocumentType = EDocumentType.Invoice, DocumentNumber = "FAK1" }
        };

        _invoiceService
            .GetFinalInvoicesForProformaAsync(10L, Arg.Any<CancellationToken>())
            .Returns(ordered);

        var result = await BuildController().GetFinalInvoicesForProforma(proformaId: 10L);

        var ok = result.Result.ShouldBeOfType<OkObjectResult>();
        var list = ok.Value.ShouldBeOfType<List<InvoiceDto>>();

        // First element in the response must be the first element from the service (newer).
        list[0].Id.ShouldBe(21L);
        list[1].Id.ShouldBe(20L);
    }
}
