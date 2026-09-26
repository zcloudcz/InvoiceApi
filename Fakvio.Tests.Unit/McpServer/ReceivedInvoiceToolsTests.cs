using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using System.Net;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Configuration;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for <see cref="ReceivedInvoiceTools"/> — the MCP tools over incoming
/// (supplier) invoices. Same harness as <c>InvoiceToolsTests</c>: the tool methods
/// are static and take <see cref="IFakvioApiClient"/> as their first argument.
///
/// Junior note: every tool wraps its body in try/catch and serializes non-cancellation
/// failures as a sanitized <c>{ "error": "internal_error", "message": "..." }</c> via
/// <see cref="McpToolError"/> — never the raw exception message (issue #279). A test
/// asserts on four things:
///   1. what the tool forwarded to the API client (filter mapping, parsed enums/dates),
///   2. the JSON it produced on success,
///   3. that a thrown API exception comes back as a sanitized "error" property instead of
///      bubbling out of the tool (an MCP tool that throws kills the whole call) — and that
///      the exception's own message never leaks into that JSON,
///   4. that <see cref="OperationCanceledException"/> is the one exception that DOES
///      propagate instead of being turned into an "error" JSON.
/// </summary>
public class ReceivedInvoiceToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    /// <summary>
    /// Helper for the very common "the API returns an empty page" arrangement —
    /// used whenever the assertion is about the filter, not about the payload.
    /// </summary>
    private void ArrangeEmptyPage() =>
        _api.GetReceivedInvoicesPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ReceivedInvoiceDto>());

    // ── ListReceivedInvoices ───────────────────────────────────────────

    [Fact]
    public async Task ListReceivedInvoices_ReturnsPagedJson()
    {
        // Arrange
        var page = new PagedResult<ReceivedInvoiceDto>(
            [new ReceivedInvoiceDto
            {
                Id = 1,
                DocumentNumber = "PF-2026-001",
                SupplierName = "Dodavatel s.r.o.",
                TotalWithVat = 12_100m,
                Status = EReceivedInvoiceStatus.Received
            }],
            totalCount: 1, pageNumber: 1, pageSize: 20);

        _api.GetReceivedInvoicesPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(page);

        // Act
        var json = await ReceivedInvoiceTools.ListReceivedInvoices(_api, page: 1, pageSize: 20);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(1);
        var first = doc.RootElement.GetProperty("items")[0];
        first.GetProperty("documentNumber").GetString().ShouldBe("PF-2026-001");
        first.GetProperty("supplierName").GetString().ShouldBe("Dodavatel s.r.o.");
        // JsonStringEnumConverter is configured, so the status is a name, not a number.
        first.GetProperty("status").GetString().ShouldBe("Received");
    }

    [Fact]
    public async Task ListReceivedInvoices_ParsesStatusEnum_CaseInsensitive()
    {
        // Arrange
        ArrangeEmptyPage();

        // Act: lowercase input — the tool parses with ignoreCase
        await ReceivedInvoiceTools.ListReceivedInvoices(_api, status: "approved");

        // Assert
        await _api.Received(1).GetReceivedInvoicesPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.Status == EReceivedInvoiceStatus.Approved),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReceivedInvoices_UnknownStatus_LeavesFilterUnset()
    {
        // Arrange
        ArrangeEmptyPage();

        // Act: a status the enum does not know must not blow up the whole listing
        await ReceivedInvoiceTools.ListReceivedInvoices(_api, status: "Bogus");

        // Assert: no status filter at all (better than silently filtering on garbage)
        await _api.Received(1).GetReceivedInvoicesPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.Status == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReceivedInvoices_ForwardsPage_AndCapsPageSizeAt100()
    {
        // Arrange
        ArrangeEmptyPage();

        // Act
        await ReceivedInvoiceTools.ListReceivedInvoices(_api, page: 3, pageSize: 500);

        // Assert: page passes through untouched, pageSize is clamped.
        // Junior note: the clamp is asserted as an end-to-end contract, not as a test
        // of the tool's own Math.Min — PaginationParams.PageSize clamps to 100 in its
        // setter as well, so the two guards are fully redundant on this path: deleting
        // either one on its own keeps this test green, only removing both makes it fail.
        // The setter is pinned separately by PaginationParamsTests, so a regression there
        // is caught by that suite, not by this one.
        await _api.Received(1).GetReceivedInvoicesPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.PageSize == 100 && f.Page == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReceivedInvoices_ParsesIssueDateRange()
    {
        // Arrange
        ArrangeEmptyPage();

        // Act
        await ReceivedInvoiceTools.ListReceivedInvoices(
            _api, issueDateFrom: "2026-01-01", issueDateTo: "2026-01-31");

        // Assert
        await _api.Received(1).GetReceivedInvoicesPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f =>
                f.IssueDateFrom == new DateTime(2026, 1, 1) &&
                f.IssueDateTo == new DateTime(2026, 1, 31)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReceivedInvoices_UnparsableDate_LeavesFilterUnset()
    {
        // Arrange
        ArrangeEmptyPage();

        // Act: garbage in the date field is ignored rather than failing the call
        await ReceivedInvoiceTools.ListReceivedInvoices(
            _api, issueDateFrom: "not-a-date", issueDateTo: "2026-01-31");

        // Assert
        await _api.Received(1).GetReceivedInvoicesPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f =>
                f.IssueDateFrom == null &&
                f.IssueDateTo == new DateTime(2026, 1, 31)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReceivedInvoices_ForwardsSearchSupplierOverdue_AndFixedSort()
    {
        // Arrange
        ArrangeEmptyPage();

        // Act
        await ReceivedInvoiceTools.ListReceivedInvoices(
            _api, search: "Acme", supplierId: 42, isOverdue: true);

        // Assert: sort is hard-coded by the tool — newest received first
        await _api.Received(1).GetReceivedInvoicesPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f =>
                f.Search == "Acme" &&
                f.SupplierId == 42 &&
                f.IsOverdue == true &&
                f.SortBy == "ReceivedDate" &&
                f.SortDirection == "desc"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListReceivedInvoices_ReturnsError_OnApiFailure()
    {
        // Arrange
        _api.GetReceivedInvoicesPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        // Act
        var json = await ReceivedInvoiceTools.ListReceivedInvoices(_api);

        // Assert: sanitized error, the raw exception message must not leak (issue #279)
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("Connection refused");
    }

    [Fact]
    public async Task ListReceivedInvoices_PropagatesCancellation_WhenTheCallerCancelled()
    {
        // A request the caller cancelled is not a domain error — the tool must let it
        // bubble out instead of turning it into a fake "error" JSON result (issue #279).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _api.GetReceivedInvoicesPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => ReceivedInvoiceTools.ListReceivedInvoices(_api, ct: cts.Token));
    }

    [Fact]
    public async Task ListReceivedInvoices_ReturnsSanitizedError_OnHttpClientTimeout()
    {
        // HttpClient throws TaskCanceledException (a subclass of OperationCanceledException)
        // on its OWN timeout, and then the caller's token was never cancelled. That is an
        // API-side failure, not a cancellation, so it has to come back as sanitized JSON:
        // an MCP tool that throws kills the whole call (issue #279).
        using var cts = new CancellationTokenSource();
        _api.GetReceivedInvoicesPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Throws(new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()));

        var json = await ReceivedInvoiceTools.ListReceivedInvoices(_api, ct: cts.Token);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("HttpClient.Timeout");
    }

    // ── GetReceivedInvoice ─────────────────────────────────────────────

    [Fact]
    public async Task GetReceivedInvoice_ReturnsInvoiceJson()
    {
        // Arrange
        _api.GetReceivedInvoiceByIdAsync(42, Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto
            {
                Id = 42,
                DocumentNumber = "PF-2026-042",
                Status = EReceivedInvoiceStatus.Approved,
                TotalWithVat = 24_200m
            });

        // Act
        var json = await ReceivedInvoiceTools.GetReceivedInvoice(_api, 42);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(42);
        doc.RootElement.GetProperty("documentNumber").GetString().ShouldBe("PF-2026-042");
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Approved");
    }

    [Fact]
    public async Task GetReceivedInvoice_ReturnsError_WhenNotFound()
    {
        // Arrange: the API client returns null for a missing invoice (no exception)
        _api.GetReceivedInvoiceByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((ReceivedInvoiceDto?)null);

        // Act
        var json = await ReceivedInvoiceTools.GetReceivedInvoice(_api, 999);

        // Assert: the message names the id so the model can echo it back to the user
        var error = JsonDocument.Parse(json).RootElement.GetProperty("error").GetString();
        error.ShouldContain("not found");
        error.ShouldContain("999");
    }

    [Fact]
    public async Task GetReceivedInvoice_ReturnsError_OnApiFailure()
    {
        // Arrange
        _api.GetReceivedInvoiceByIdAsync(7, Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("502 Bad Gateway"));

        // Act
        var json = await ReceivedInvoiceTools.GetReceivedInvoice(_api, 7);

        // Assert: sanitized error, the raw exception message must not leak (issue #279)
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
        root.GetProperty("message").GetString().ShouldNotContain("502 Bad Gateway");
    }

    // ── CreateReceivedInvoice ──────────────────────────────────────────

    [Fact]
    public async Task CreateReceivedInvoice_CreatesFromTypedDto_ResolvesCurrency()
    {
        // Arrange
        _api.CreateReceivedInvoiceAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 10, Status = EReceivedInvoiceStatus.Received });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" }, new() { Id = 3, Code = "EUR" } });

        var invoice = new CreateReceivedInvoiceDto
        {
            SupplierId = 3,
            DocumentNumber = "PF-2026-010",
            Items =
            [
                new() { Description = "Hosting", Quantity = 2, UnitPrice = 500, VatRatePercentage = 21 }
            ]
        };

        // Act — N2.5: typed DTO parameter; currency is a separate code parameter, defaults to CZK.
        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, invoice);

        // Assert: result is the created invoice ...
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(10);

        // ... and CurrencyId was resolved from the (default) CZK code, overriding whatever
        // the caller may have set on the DTO — currency is the source of truth (N2.4/N2.5).
        await _api.Received(1).CreateReceivedInvoiceAsync(
            Arg.Is<CreateReceivedInvoiceDto>(d =>
                d.SupplierId == 3 &&
                d.CurrencyId == 1 &&
                d.DocumentNumber == "PF-2026-010" &&
                d.Items.Count == 1 &&
                d.Items[0].Description == "Hosting" &&
                d.Items[0].Quantity == 2m &&
                d.Items[0].UnitPrice == 500m &&
                d.Items[0].VatRatePercentage == 21m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateReceivedInvoice_AcceptsCurrencyCode_ResolvesToCurrencyId()
    {
        _api.CreateReceivedInvoiceAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 11 });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" }, new() { Id = 3, Code = "EUR" } });

        var invoice = new CreateReceivedInvoiceDto
        {
            SupplierId = 3,
            Items = [new() { Description = "Hosting", Quantity = 1, UnitPrice = 100 }]
        };

        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, invoice, currency: "eur");

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(11);
        await _api.Received(1).CreateReceivedInvoiceAsync(
            Arg.Is<CreateReceivedInvoiceDto>(d => d.CurrencyId == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateReceivedInvoice_ReturnsError_OnNullInvoice()
    {
        // Act: null DTO hits the explicit guard before any API call
        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, null!);

        // Assert
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .ShouldContain("invoice is required");
        await _api.DidNotReceive().CreateReceivedInvoiceAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateReceivedInvoice_UnknownCurrency_ReturnsErrorWithoutCallingApi()
    {
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });

        var invoice = new CreateReceivedInvoiceDto
        {
            SupplierId = 3,
            Items = [new() { Description = "Hosting", Quantity = 1, UnitPrice = 100 }]
        };

        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, invoice, currency: "XYZ");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("Unknown currency 'XYZ'");
        await _api.DidNotReceive().CreateReceivedInvoiceAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateReceivedInvoice_ReturnsError_OnApiFailure()
    {
        // Arrange: server-side validation rejects the invoice
        _api.CreateReceivedInvoiceAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("400 Bad Request: supplier not found"));
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });

        var invoice = new CreateReceivedInvoiceDto { SupplierId = 999, Items = [] };

        // Act
        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, invoice);

        // Assert: sanitized error, the raw exception message must not leak (issue #279)
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
        root.GetProperty("message").GetString().ShouldNotContain("supplier not found");
    }

    // ── ApproveReceivedInvoice ─────────────────────────────────────────

    [Fact]
    public async Task ApproveReceivedInvoice_ReturnsApprovedInvoice()
    {
        // Arrange
        _api.ApproveReceivedInvoiceAsync(5, Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto
            {
                Id = 5,
                DocumentNumber = "PF-2026-005",
                Status = EReceivedInvoiceStatus.Approved
            });

        // Act
        var json = await ReceivedInvoiceTools.ApproveReceivedInvoice(_api, 5);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(5);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Approved");
        await _api.Received(1).ApproveReceivedInvoiceAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ApproveReceivedInvoice_ReturnsError_OnInvalidTransition()
    {
        // Arrange: the API rejects approving an invoice that is not in Received status
        _api.ApproveReceivedInvoiceAsync(5, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Only invoices in Received status can be approved."));

        // Act
        var json = await ReceivedInvoiceTools.ApproveReceivedInvoice(_api, 5);

        // Assert: sanitized error, the raw exception message must not leak (issue #279)
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
        root.GetProperty("message").GetString().ShouldNotContain("Received status");
    }

    // ── MarkReceivedInvoicePaid ────────────────────────────────────────

    [Fact]
    public async Task MarkReceivedInvoicePaid_ReturnsPaidInvoice()
    {
        // Arrange
        var paidAt = new DateTime(2026, 2, 1);
        _api.MarkReceivedInvoicePaidAsync(8, Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto
            {
                Id = 8,
                Status = EReceivedInvoiceStatus.Paid,
                PaidAt = paidAt
            });

        // Act
        var json = await ReceivedInvoiceTools.MarkReceivedInvoicePaid(_api, 8);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Paid");
        doc.RootElement.GetProperty("paidAt").GetDateTime().ShouldBe(paidAt);
        await _api.Received(1).MarkReceivedInvoicePaidAsync(8, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkReceivedInvoicePaid_ReturnsError_OnInvalidTransition()
    {
        // Arrange
        _api.MarkReceivedInvoicePaidAsync(8, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Only invoices in Approved status can be marked as paid."));

        // Act
        var json = await ReceivedInvoiceTools.MarkReceivedInvoicePaid(_api, 8);

        // Assert: sanitized error, the raw exception message must not leak (issue #279)
        var root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().ShouldBe("internal_error");
        root.GetProperty("message").GetString().ShouldNotContain("Approved status");
    }

    // ── DeleteReceivedInvoice ──────────────────────────────────────────

    [Fact]
    public async Task DeleteReceivedInvoice_ReturnsSuccessMessage()
    {
        // Arrange
        _api.DeleteReceivedInvoiceAsync(7, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Act
        var json = await ReceivedInvoiceTools.DeleteReceivedInvoice(_api, 7);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("message").GetString().ShouldContain("7");
        await _api.Received(1).DeleteReceivedInvoiceAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteReceivedInvoice_ReturnsError_WhenApiRejectsDelete()
    {
        // Arrange: only Received/Rejected invoices may be deleted — the API enforces it
        _api.DeleteReceivedInvoiceAsync(7, Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Paid invoices cannot be deleted."));

        // Act
        var json = await ReceivedInvoiceTools.DeleteReceivedInvoice(_api, 7);

        // Assert: the failure must not be reported as success, and the raw exception
        // message must not leak (issue #279)
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("success", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("cannot be deleted");
    }

    [Fact]
    public async Task DeleteReceivedInvoice_PropagatesCancellation_WhenTheCallerCancelled()
    {
        // A request the caller cancelled is not a domain error — the tool must let it
        // bubble out instead of turning it into a fake "error" JSON result (issue #279).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _api.DeleteReceivedInvoiceAsync(7, Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => ReceivedInvoiceTools.DeleteReceivedInvoice(_api, 7, ct: cts.Token));
    }

    [Fact]
    public async Task DeleteReceivedInvoice_ReturnsSanitizedError_OnHttpClientTimeout()
    {
        // HttpClient throws TaskCanceledException (a subclass of OperationCanceledException)
        // on its OWN timeout, and then the caller's token was never cancelled. That is an
        // API-side failure, not a cancellation, so it has to come back as sanitized JSON:
        // an MCP tool that throws kills the whole call (issue #279).
        using var cts = new CancellationTokenSource();
        _api.DeleteReceivedInvoiceAsync(7, Arg.Any<CancellationToken>())
            .Throws(new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()));

        var json = await ReceivedInvoiceTools.DeleteReceivedInvoice(_api, 7, ct: cts.Token);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("success", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("HttpClient.Timeout");
    }

    // N2.5: "paymentMethod is not an EPaymentMethod" used to be a JSON-parsing concern of THIS
    // tool (it deserialized the model's raw JSON itself). Now that the parameter is a typed
    // CreateReceivedInvoiceDto, that deserialization — and therefore an invalid enum string —
    // is the SDK's job before this method is even invoked. Covered by
    // McpSdkInvocationTests.InvokeAsync_AcceptsEnumAsString_ForTypedDtoParameter (real SDK path)
    // and ToolDiscoveryTests' no-JSON-string-parameter guard.

    // ── UploadReceivedInvoiceAttachment ────────────────────────────────

    private static readonly byte[] Bytes = [1, 2, 3];

    private static McpServerSettings Local(bool allow) => new() { AllowLocalFiles = allow };

    /// <summary>Factory whose client answers every request with the given response.</summary>
    private static IHttpClientFactory Http(HttpResponseMessage? response = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(new HttpClient(new StubHandler(response ?? new HttpResponseMessage())));
        return factory;
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    private void ArrangeUploadOk(string fileName) =>
        _api.UploadFileAttachmentAsync("ReceivedInvoice", 27, fileName, "application/pdf",
                Arg.Is<byte[]>(b => b.SequenceEqual(Bytes)), null, Arg.Any<CancellationToken>())
            .Returns(new FileAttachmentDto { Id = 5, EntityName = "ReceivedInvoice", RecordId = 27, OriginalFileName = fileName });

    [Fact]
    public async Task UploadReceivedInvoiceAttachment_DecodesBase64AndCallsApi()
    {
        ArrangeUploadOk("a.pdf");

        var json = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
            _api, Local(false), Http(), 27, base64Content: Convert.ToBase64String(Bytes), fileName: "a.pdf");

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(5);
    }

    [Fact]
    public async Task UploadReceivedInvoiceAttachment_ReturnsError_OnInvalidBase64()
    {
        var json = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
            _api, Local(false), Http(), 27, base64Content: "not base64!");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("base64");
        await _api.DidNotReceiveWithAnyArgs().UploadFileAttachmentAsync(default!, default, default!, default!, default!);
    }

    [Fact]
    public async Task UploadReceivedInvoiceAttachment_DownloadsFromUrl_AndDerivesFileName()
    {
        ArrangeUploadOk("4025178692.pdf");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };

        var json = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
            _api, Local(false), Http(response), 27, fileUrl: "https://www.alza.cz/invoices/4025178692.pdf");

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(5);
    }

    [Theory]
    [InlineData("http://example.com/a.pdf")]      // plain http
    [InlineData("https://127.0.0.1/a.pdf")]       // IP literal — SSRF guard
    [InlineData("https://localhost/a.pdf")]       // loopback
    [InlineData("file:///C:/a.pdf")]
    public async Task UploadReceivedInvoiceAttachment_RejectsUnsafeUrls(string url)
    {
        var json = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
            _api, Local(false), Http(), 27, fileUrl: url);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("fileUrl");
        await _api.DidNotReceiveWithAnyArgs().UploadFileAttachmentAsync(default!, default, default!, default!, default!);
    }

    [Fact]
    public async Task UploadReceivedInvoiceAttachment_RejectsOversizedDownload()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        response.Content.Headers.ContentLength = 51L * 1024 * 1024;

        var json = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
            _api, Local(false), Http(response), 27, fileUrl: "https://example.com/big.pdf");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("50 MB");
    }

    [Fact]
    public async Task UploadReceivedInvoiceAttachment_ReadsLocalFile_WhenAllowed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fakvio-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(path, Bytes);
        try
        {
            ArrangeUploadOk(Path.GetFileName(path));

            var json = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
                _api, Local(true), Http(), 27, filePath: path);

            JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UploadReceivedInvoiceAttachment_RefusesLocalFile_OnRemoteHost()
    {
        // HTTP host: the disk is the shared server's, not the caller's — must never be read.
        var json = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
            _api, Local(false), Http(), 27, filePath: @"C:\Windows\win.ini");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("stdio");
        await _api.DidNotReceiveWithAnyArgs().UploadFileAttachmentAsync(default!, default, default!, default!, default!);
    }

    [Fact]
    public async Task UploadReceivedInvoiceAttachment_RequiresExactlyOneSource()
    {
        var none = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(_api, Local(true), Http(), 27);
        var two = await ReceivedInvoiceTools.UploadReceivedInvoiceAttachment(
            _api, Local(true), Http(), 27, fileUrl: "https://e.com/a.pdf", base64Content: "AQID");

        JsonDocument.Parse(none).RootElement.GetProperty("error").GetString().ShouldContain("exactly one");
        JsonDocument.Parse(two).RootElement.GetProperty("error").GetString().ShouldContain("exactly one");
    }
}
