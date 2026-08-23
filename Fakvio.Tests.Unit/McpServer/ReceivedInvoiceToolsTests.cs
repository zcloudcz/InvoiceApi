using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for <see cref="ReceivedInvoiceTools"/> — the MCP tools over incoming
/// (supplier) invoices. Same harness as <c>InvoiceToolsTests</c>: the tool methods
/// are static, take <see cref="IFakvioApiClient"/> as their first argument, and
/// always return a JSON string — never throw.
///
/// Junior note: every tool wraps its body in try/catch and serializes failures as
/// <c>{ "error": "..." }</c>. So a test asserts on three things:
///   1. what the tool forwarded to the API client (filter mapping, parsed enums/dates),
///   2. the JSON it produced on success,
///   3. that a thrown API exception comes back as an "error" property instead of
///      bubbling out of the tool (an MCP tool that throws kills the whole call).
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
        // Junior note: the clamp is asserted as an end-to-end contract, not as a
        // test of the tool's own Math.Min — PaginationParams.PageSize clamps to 100
        // in its setter as well, so the two guards overlap. Deleting the tool's
        // Math.Min would keep this test green; deleting the setter guard would not.
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

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Connection refused");
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

        // Assert
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .ShouldContain("502 Bad Gateway");
    }

    // ── CreateReceivedInvoice ──────────────────────────────────────────

    [Fact]
    public async Task CreateReceivedInvoice_DeserializesJsonAndCreates()
    {
        // Arrange
        _api.CreateReceivedInvoiceAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedInvoiceDto { Id = 10, Status = EReceivedInvoiceStatus.Received });

        var invoiceJson = JsonSerializer.Serialize(new
        {
            supplierId = 3,
            currencyId = 1,
            documentNumber = "PF-2026-010",
            items = new[]
            {
                new { description = "Hosting", quantity = 2, unitPrice = 500, vatRatePercentage = 21 }
            }
        });

        // Act
        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, invoiceJson);

        // Assert: result is the created invoice ...
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(10);

        // ... and the JSON really was mapped onto the DTO, not passed through empty
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
    public async Task CreateReceivedInvoice_ReturnsError_OnMalformedJson()
    {
        // Act: not JSON at all — the deserializer throws and the tool must catch it
        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, "this is not json");

        // Assert: an error is reported and nothing was sent to the API
        JsonDocument.Parse(json).RootElement.TryGetProperty("error", out _).ShouldBeTrue();
        await _api.DidNotReceive().CreateReceivedInvoiceAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateReceivedInvoice_ReturnsError_OnJsonNullLiteral()
    {
        // Act: valid JSON that deserializes to null — hits the explicit null guard,
        // a different code path than the malformed-JSON case above
        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(_api, "null");

        // Assert
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .ShouldContain("Failed to parse");
        await _api.DidNotReceive().CreateReceivedInvoiceAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateReceivedInvoice_ReturnsError_OnApiFailure()
    {
        // Arrange: server-side validation rejects the invoice
        _api.CreateReceivedInvoiceAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("400 Bad Request: supplier not found"));

        // Act
        var json = await ReceivedInvoiceTools.CreateReceivedInvoice(
            _api, "{\"supplierId\":999,\"currencyId\":1,\"items\":[]}");

        // Assert
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .ShouldContain("supplier not found");
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

        // Assert
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .ShouldContain("Received status");
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

        // Assert
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .ShouldContain("Approved status");
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

        // Assert: the failure must not be reported as success
        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("success", out _).ShouldBeFalse();
        doc.RootElement.GetProperty("error").GetString().ShouldContain("cannot be deleted");
    }
}
