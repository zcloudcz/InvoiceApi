// ============================================================================
// InvoiceControllerGetErrorHandlingTests — coverage for issue #99.
//
// Verifies that InvoiceController GET endpoints return HTTP 500 with a
// structured error body (not an uncaught exception) when the service layer
// throws. Before the fix, exceptions propagated unhandled → bare HTTP 500
// with empty body from ASP.NET Core's default exception handler.
//
// Focus: GetAllInvoices, GetInvoicesPaged, GetInvoiceById, GetInvoiceByDocumentNumber,
//        GetCreditNotesForInvoice, GetFinalInvoicesForProforma, GetTaxReceiptsForProforma,
//        GetRemainingAdvance.
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
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests that every GET endpoint in InvoiceController catches exceptions from the
/// service layer and returns HTTP 500 with a structured JSON body instead of
/// propagating the exception uncaught.
///
/// Why this matters: When the EF Core query fails (e.g., missing DB column after
/// a failed tenant migration), the old code returned a bare HTTP 500 with no body.
/// The client logged "HTTP 500 InternalServerError" with no actionable info.
/// The fix wraps each service call in try-catch, logs the error, and returns a
/// structured error response so both client and server have context.
/// </summary>
public class InvoiceControllerGetErrorHandlingTests
{
    private readonly IInvoiceService _invoiceService = Substitute.For<IInvoiceService>();
    private readonly ILogger<InvoiceController> _logger = Substitute.For<ILogger<InvoiceController>>();

    /// <summary>
    /// Builds the controller under test with a minimal authenticated HTTP context.
    /// CompanyId is set on the JWT claim so the controller's auto-filter by company fires.
    /// </summary>
    private InvoiceController BuildController(long companyId = 1)
    {
        var controller = new InvoiceController(
            _invoiceService,
            Substitute.For<IPdfExportService>(),
            Substitute.For<IIsdocExportService>(),
            Substitute.For<IEmailService>(),
            Substitute.For<IQrPaymentService>(),
            Substitute.For<ICloudStorageOrchestrator>(),
            _logger);

        // Simulate an authenticated regular user with a company context
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "user-42"),
            new("CompanyId", companyId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var user = new ClaimsPrincipal(identity);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = user }
        };

        return controller;
    }

    // ─── GetAllInvoices ───────────────────────────────────────────────────────

    /// <summary>
    /// When GetAllInvoicesAsync throws (e.g., EF Core PostgresException on missing column),
    /// GetAllInvoices must return HTTP 500 with a structured message — not an uncaught exception.
    /// </summary>
    [Fact]
    public async Task GetAllInvoices_ServiceThrows_Returns500WithMessage()
    {
        // Arrange
        _invoiceService
            .GetAllInvoicesAsync(
                Arg.Any<EDocumentType?>(), Arg.Any<EInvoiceStatus?>(),
                Arg.Any<long?>(), Arg.Any<long?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("column VatRegime does not exist"));

        var controller = BuildController();

        // Act
        var result = await controller.GetAllInvoices(cancellationToken: CancellationToken.None);

        // Assert — HTTP 500 with structured body
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        objectResult.Value.ShouldNotBeNull();
        // The message property should be present and non-empty
        var value = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        value.ShouldNotBeNullOrEmpty();
    }

    // ─── GetInvoicesPaged ─────────────────────────────────────────────────────

    /// <summary>
    /// When GetInvoicesPagedAsync throws (primary failure vector per issue #99),
    /// GetInvoicesPaged must return HTTP 500 with a structured error body.
    /// This is the main regression test for the reported bug.
    /// </summary>
    [Fact]
    public async Task GetInvoicesPaged_ServiceThrows_Returns500WithMessage()
    {
        // Arrange — simulate the DB query failure that caused the original 500
        _invoiceService
            .GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("column \"VatRegime\" does not exist"));

        var controller = BuildController();
        var filter = new InvoiceFilterDto { Page = 1, PageSize = 10 };

        // Act
        var result = await controller.GetInvoicesPaged(filter, CancellationToken.None);

        // Assert — HTTP 500, not an uncaught exception
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        objectResult.Value.ShouldNotBeNull();
        var message = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// When GetInvoicesPagedAsync succeeds, GetInvoicesPaged must return HTTP 200 with data.
    /// Ensures the try-catch does not swallow successful responses.
    /// </summary>
    [Fact]
    public async Task GetInvoicesPaged_ServiceSucceeds_Returns200()
    {
        // Arrange
        var pagedResult = new Fakvio.Contracts.Common.Pagination.PagedResult<InvoiceDto>(
            new List<InvoiceDto>(), totalCount: 0, pageNumber: 1, pageSize: 10);

        _invoiceService
            .GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(pagedResult);

        var controller = BuildController();
        var filter = new InvoiceFilterDto { Page = 1, PageSize = 10 };

        // Act
        var result = await controller.GetInvoicesPaged(filter, CancellationToken.None);

        // Assert — HTTP 200 with page data
        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.StatusCode.ShouldBe(200);
        okResult.Value.ShouldBe(pagedResult);
    }

    // ─── GetInvoiceById ───────────────────────────────────────────────────────

    /// <summary>
    /// When GetInvoiceByIdAsync throws, GetInvoiceById must return HTTP 500 with a message.
    /// </summary>
    [Fact]
    public async Task GetInvoiceById_ServiceThrows_Returns500WithMessage()
    {
        // Arrange
        _invoiceService
            .GetInvoiceByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated DB error"));

        var controller = BuildController();

        // Act
        var result = await controller.GetInvoiceById(id: 42, CancellationToken.None);

        // Assert
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        var message = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
    }

    // ─── GetInvoiceByDocumentNumber ───────────────────────────────────────────

    /// <summary>
    /// When GetInvoiceByDocumentNumberAsync throws, GetInvoiceByDocumentNumber must return HTTP 500.
    /// </summary>
    [Fact]
    public async Task GetInvoiceByDocumentNumber_ServiceThrows_Returns500WithMessage()
    {
        // Arrange
        _invoiceService
            .GetInvoiceByDocumentNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated DB error"));

        var controller = BuildController();

        // Act
        var result = await controller.GetInvoiceByDocumentNumber("INV-2026-001", CancellationToken.None);

        // Assert
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        var message = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
    }

    // ─── GetCreditNotesForInvoice ─────────────────────────────────────────────

    /// <summary>
    /// When GetCreditNotesForInvoiceAsync throws, GetCreditNotesForInvoice must return HTTP 500.
    /// </summary>
    [Fact]
    public async Task GetCreditNotesForInvoice_ServiceThrows_Returns500WithMessage()
    {
        // Arrange
        _invoiceService
            .GetCreditNotesForInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated DB error"));

        var controller = BuildController();

        // Act
        var result = await controller.GetCreditNotesForInvoice(invoiceId: 42, CancellationToken.None);

        // Assert
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        var message = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
    }

    // ─── GetFinalInvoicesForProforma ──────────────────────────────────────────

    /// <summary>
    /// When GetFinalInvoicesForProformaAsync throws, GetFinalInvoicesForProforma must return HTTP 500.
    /// </summary>
    [Fact]
    public async Task GetFinalInvoicesForProforma_ServiceThrows_Returns500WithMessage()
    {
        // Arrange
        _invoiceService
            .GetFinalInvoicesForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated DB error"));

        var controller = BuildController();

        // Act
        var result = await controller.GetFinalInvoicesForProforma(proformaId: 1, CancellationToken.None);

        // Assert
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        var message = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
    }

    // ─── GetTaxReceiptsForProforma ────────────────────────────────────────────

    /// <summary>
    /// When GetTaxReceiptsForProformaAsync throws, GetTaxReceiptsForProforma must return HTTP 500.
    /// </summary>
    [Fact]
    public async Task GetTaxReceiptsForProforma_ServiceThrows_Returns500WithMessage()
    {
        // Arrange
        _invoiceService
            .GetTaxReceiptsForProformaAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated DB error"));

        var controller = BuildController();

        // Act
        var result = await controller.GetTaxReceiptsForProforma(proformaId: 1, CancellationToken.None);

        // Assert
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        var message = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
    }

    // ─── GetRemainingAdvance ──────────────────────────────────────────────────

    /// <summary>
    /// When GetRemainingAdvanceAsync throws, GetRemainingAdvance must return HTTP 500.
    /// </summary>
    [Fact]
    public async Task GetRemainingAdvance_ServiceThrows_Returns500WithMessage()
    {
        // Arrange
        _invoiceService
            .GetRemainingAdvanceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("simulated DB error"));

        var controller = BuildController();

        // Act
        var result = await controller.GetRemainingAdvance(proformaId: 1, CancellationToken.None);

        // Assert
        var objectResult = result.Result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(500);
        var message = objectResult.Value!.GetType().GetProperty("message")?.GetValue(objectResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
    }

    // ─── GetInvoiceById — 404 path ───────────────────────────────────────────

    /// <summary>
    /// When GetInvoiceByIdAsync returns null (invoice not found), GetInvoiceById
    /// must return HTTP 404 with a structured "Invoice with ID {id} not found" message.
    ///
    /// This verifies the 404 path is preserved inside the try-catch block —
    /// the refactoring moved the null-check inside the try, so we confirm
    /// NotFound still fires correctly.
    /// </summary>
    [Fact]
    public async Task GetInvoiceById_ServiceReturnsNull_Returns404WithMessage()
    {
        // Arrange — service returns null (invoice does not exist for this tenant)
        _invoiceService
            .GetInvoiceByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns((InvoiceDto?)null);

        var controller = BuildController();

        // Act
        var result = await controller.GetInvoiceById(id: 9999, CancellationToken.None);

        // Assert — 404 with structured body containing the ID
        var notFoundResult = result.Result.ShouldBeOfType<NotFoundObjectResult>();
        notFoundResult.StatusCode.ShouldBe(404);
        var message = notFoundResult.Value!.GetType().GetProperty("message")?.GetValue(notFoundResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
        // 404 message should include the requested invoice ID so the client knows which resource was missing
        message.ShouldContain("9999");
    }

    /// <summary>
    /// When GetInvoiceByIdAsync returns a valid invoice, GetInvoiceById must return
    /// HTTP 200 with the invoice DTO — the try-catch must not interfere with happy path.
    /// </summary>
    [Fact]
    public async Task GetInvoiceById_ServiceReturnsInvoice_Returns200()
    {
        // Arrange
        var invoiceDto = new InvoiceDto { Id = 42, DocumentNumber = "INV-2026-001" };
        _invoiceService
            .GetInvoiceByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(invoiceDto);

        var controller = BuildController();

        // Act
        var result = await controller.GetInvoiceById(id: 42, CancellationToken.None);

        // Assert
        var okResult = result.Result.ShouldBeOfType<OkObjectResult>();
        okResult.StatusCode.ShouldBe(200);
        okResult.Value.ShouldBe(invoiceDto);
    }

    // ─── GetInvoiceByDocumentNumber — 404 path ───────────────────────────────

    /// <summary>
    /// When GetInvoiceByDocumentNumberAsync returns null (document number not found),
    /// GetInvoiceByDocumentNumber must return HTTP 404 with a structured message
    /// containing the document number.
    /// </summary>
    [Fact]
    public async Task GetInvoiceByDocumentNumber_ServiceReturnsNull_Returns404WithMessage()
    {
        // Arrange
        _invoiceService
            .GetInvoiceByDocumentNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((InvoiceDto?)null);

        var controller = BuildController();

        // Act
        var result = await controller.GetInvoiceByDocumentNumber("INV-MISSING-001", CancellationToken.None);

        // Assert — 404 with the document number in the message
        var notFoundResult = result.Result.ShouldBeOfType<NotFoundObjectResult>();
        notFoundResult.StatusCode.ShouldBe(404);
        var message = notFoundResult.Value!.GetType().GetProperty("message")?.GetValue(notFoundResult.Value) as string;
        message.ShouldNotBeNullOrEmpty();
        // 404 message should echo the document number so the caller knows which document was missing
        message.ShouldContain("INV-MISSING-001");
    }
}
