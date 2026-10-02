using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.McpServer.Client;
using Fakvio.McpServer.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit.McpServer;

/// <summary>
/// Tests for MCP tool classes (InvoiceTools, ClientTools, TemplateTools, ReportingTools).
/// Uses NSubstitute to mock IFakvioApiClient.
///
/// Junior note: Each MCP tool method is static and receives IFakvioApiClient
/// via DI. We mock the interface and verify:
///   1. Correct parameters are passed to the API client
///   2. Results are properly JSON-serialized
///   3. Errors are returned as JSON with "error" property
///   4. Enum string parsing works correctly
/// </summary>
public class InvoiceToolsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    // ── InvoiceTools tests ─────────────────────────────────────────────

    [Fact]
    public async Task ListInvoices_ReturnsJsonResult()
    {
        // Arrange
        var pagedResult = new PagedResult<InvoiceDto>(
            [new InvoiceDto { Id = 1, DocumentNumber = "FAK001", TotalWithVat = 12100m }],
            totalCount: 1, pageNumber: 1, pageSize: 20);

        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(pagedResult);

        // Act
        var json = await InvoiceTools.ListInvoices(_api, page: 1, pageSize: 20);

        // Assert: verify JSON contains expected data
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("items").GetArrayLength().ShouldBe(1);
        doc.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task ListInvoices_ParsesDocumentTypeEnum()
    {
        // Arrange
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        // Act
        await InvoiceTools.ListInvoices(_api, documentType: "CreditNote");

        // Assert: verify the filter has the correct enum value
        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f => f.DocumentType == EDocumentType.CreditNote),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInvoices_ParsesStatusEnum_CaseInsensitive()
    {
        // Arrange
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        // Act: pass lowercase status — should still parse
        await InvoiceTools.ListInvoices(_api, status: "completed");

        // Assert
        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f => f.Status == EInvoiceStatus.Completed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInvoices_CapsPageSizeAt100()
    {
        // Arrange
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        // Act: request 200 items
        await InvoiceTools.ListInvoices(_api, pageSize: 200);

        // Assert: should be capped at 100
        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f => f.PageSize == 100),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInvoices_NoSortBy_LeavesSortToApiDefault()
    {
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        await InvoiceTools.ListInvoices(_api);

        // Null SortBy = the API's default order (newest issue date first)
        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f => f.SortBy == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListInvoices_PassesSortByAndDirection()
    {
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        await InvoiceTools.ListInvoices(_api, sortBy: "DueDate", sortDirection: "asc");

        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f => f.SortBy == "DueDate" && f.SortDirection == "asc"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInvoice_ReturnsInvoiceJson()
    {
        // Arrange
        var invoice = new InvoiceDto { Id = 42, DocumentNumber = "FAK2026001", TotalWithVat = 24200m };
        _api.GetInvoiceByIdAsync(42, Arg.Any<CancellationToken>()).Returns(invoice);

        // Act
        var json = await InvoiceTools.GetInvoice(_api, 42);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(42);
        doc.RootElement.GetProperty("documentNumber").GetString().ShouldBe("FAK2026001");
    }

    [Fact]
    public async Task GetInvoice_ReturnsError_WhenNotFound()
    {
        // Arrange
        _api.GetInvoiceByIdAsync(999, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        // Act
        var json = await InvoiceTools.GetInvoice(_api, 999);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("not found");
    }

    [Fact]
    public async Task FindInvoiceByNumber_ReturnsInvoice()
    {
        // Arrange
        var invoice = new InvoiceDto { Id = 5, DocumentNumber = "FAK2026001" };
        _api.GetInvoiceByDocumentNumberAsync("FAK2026001", Arg.Any<CancellationToken>()).Returns(invoice);

        // Act
        var json = await InvoiceTools.FindInvoiceByNumber(_api, "FAK2026001");

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("documentNumber").GetString().ShouldBe("FAK2026001");
    }

    [Fact]
    public async Task CreateInvoice_DeserializesJsonAndCreates()
    {
        // Arrange
        var created = new InvoiceDto { Id = 10, Status = EInvoiceStatus.Draft };
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(created);
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsIssuer = true, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" }, new() { Id = 3, Code = "EUR" } });

        var items = new List<CreateInvoiceItemDto>
        {
            new() { Description = "Test", Quantity = 1, Unit = "pcs", UnitPrice = 100, VatRatePercentage = 21 }
        };

        // Act — no issuerId, no currency: defaults to the authenticated issuer and CZK.
        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(10);
        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.ClientId == 1 && d.IssuerId == 2 && d.CurrencyId == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_AcceptsCurrencyCode_ResolvesToCurrencyId()
    {
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 11 });
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" }, new() { Id = 3, Code = "EUR" } });

        var items = new List<CreateInvoiceItemDto>
        {
            new() { Description = "Consulting", Quantity = 1, Unit = "pcs", UnitPrice = 100 }
        };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items, currency: "eur");

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(11);
        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.CurrencyId == 3), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_UnknownCurrency_ReturnsErrorWithoutCallingApi()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });

        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items, currency: "XYZ");

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Unknown currency 'XYZ'");
        doc.RootElement.GetProperty("error").GetString().ShouldContain("CZK");
        await _api.DidNotReceive().CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_VatPayer_ResolvesVatRateIdFromPercentage()
    {
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 12 });
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = true });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
        _api.GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VatRateDto> { new() { Id = 7, Rate = 21m }, new() { Id = 8, Rate = 0m } });

        var items = new List<CreateInvoiceItemDto>
        {
            new() { Description = "Consulting", Quantity = 1, UnitPrice = 100, VatRatePercentage = 21 }
        };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items);

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(12);
        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.InvoiceItem.Single().VatRateId == 7),
            Arg.Any<CancellationToken>());
    }

    /// <summary>EU OSS: a German 19 % must not be looked up in the CZ rate table — the server validates it.</summary>
    [Fact]
    public async Task CreateInvoice_OssInvoice_SkipsCzechRateLookup_AndSendsPercentage()
    {
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 13, OssCountryCode = "DE" });
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = true });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
        _api.GetOssCountryAsync(5, 2, EDocumentType.Invoice, Arg.Any<CancellationToken>()).Returns("DE");

        var items = new List<CreateInvoiceItemDto>
        {
            new() { Description = "Consulting", Quantity = 1, UnitPrice = 100, VatRatePercentage = 19 }
        };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 5, items: items, applyOss: true);

        JsonDocument.Parse(json).RootElement.GetProperty("ossCountryCode").GetString().ShouldBe("DE");
        await _api.DidNotReceive().GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.InvoiceItem.Single().VatRateId == null && d.InvoiceItem.Single().VatRatePercentage == 19m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOssReport_ReturnsReport_AndRejectsBadQuarter()
    {
        _api.GetOssReportAsync(2026, 1, Arg.Any<CancellationToken>())
            .Returns(new Fakvio.Contracts.Dto.OssReport.OssReportDto { Year = 2026, Quarter = 1, TotalVatEur = 12.5m });

        var ok = await ReportingTools.GetOssReport(_api, 2026, 1);
        JsonDocument.Parse(ok).RootElement.GetProperty("totalVatEur").GetDecimal().ShouldBe(12.5m);

        var bad = await ReportingTools.GetOssReport(_api, 2026, 7);
        JsonDocument.Parse(bad).RootElement.TryGetProperty("error", out _).ShouldBeTrue();
        await _api.DidNotReceive().GetOssReportAsync(2026, 7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Codex review: two active rates at the same percentage (e.g. overlapping validity periods
    /// during a rate change) must not resolve to an arbitrary one via FirstOrDefault — the model
    /// gets an explicit error naming both candidates and must disambiguate via item.vatRateId.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_VatPayer_DuplicatePercentage_ReturnsAmbiguousErrorWithoutCallingApi()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = true });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
        _api.GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VatRateDto>
            {
                new() { Id = 7, Rate = 21m, Name = "DPH 21% (staré období)", ValidFrom = new DateTime(2025, 1, 1) },
                new() { Id = 9, Rate = 21m, Name = "DPH 21% (nové období)", ValidFrom = new DateTime(2026, 1, 1) }
            });

        var items = new List<CreateInvoiceItemDto>
        {
            new() { Description = "Consulting", Quantity = 1, UnitPrice = 100, VatRatePercentage = 21 }
        };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items);

        var doc = JsonDocument.Parse(json);
        var error = doc.RootElement.GetProperty("error").GetString();
        error.ShouldContain("2 active VAT rates match 21%");
        error.ShouldContain("id=7");
        error.ShouldContain("id=9");
        error.ShouldContain("vatRateId");
        await _api.DidNotReceive().CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_VatPayer_UnknownPercentage_ReturnsErrorWithoutCallingApi()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = true });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
        _api.GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(new List<VatRateDto> { new() { Id = 7, Rate = 21m } });

        var items = new List<CreateInvoiceItemDto>
        {
            new() { Description = "Consulting", Quantity = 1, UnitPrice = 100, VatRatePercentage = 15 }
        };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("No active VAT rate matches 15%");
        await _api.DidNotReceive().CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_NonVatPayer_LeavesItemsAsIs_NoVatRateLookup()
    {
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 13 });
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });

        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items);

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(13);
        await _api.DidNotReceive().GetActiveVatRatesAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_ExplicitIssuerId_UsesGetClientById_NotGetIssuer()
    {
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 14 });
        _api.GetClientByIdAsync(9, Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 9, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });

        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items, issuerId: 9);

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(14);
        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.IssuerId == 9), Arg.Any<CancellationToken>());
        await _api.DidNotReceive().GetIssuerAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_CreditNote_PassesOriginalInvoiceId()
    {
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 15 });
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });

        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(
            _api, clientId: 1, items: items, documentType: "CreditNote", originalInvoiceId: 42);

        JsonDocument.Parse(json).RootElement.GetProperty("id").GetInt64().ShouldBe(15);
        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.DocumentType == EDocumentType.CreditNote && d.OriginalInvoiceId == 42),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_UnknownDocumentType_ReturnsErrorWithoutCallingApi()
    {
        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items, documentType: "Nonsense");

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Unknown documentType");
        await _api.DidNotReceive().CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_NoIssuerConfigured_ReturnsError()
    {
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns((ClientDto?)null);

        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("No issuer");
    }

    [Fact]
    public async Task CreateInvoice_ReturnsSanitizedError_WhenTheApiResponseCannotBeDeserialized()
    {
        // A JsonException raised while FakvioApiClient deserializes the API *response* must
        // reach the sanitized catch-all, not be echoed back as if the model's own input were
        // malformed (issue #279) — even though this tool no longer has a "parse the model's
        // JSON" step of its own.
        _api.GetIssuerAsync(Arg.Any<CancellationToken>())
            .Returns(new ClientDto { Id = 2, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Throws(new JsonException(
                "The JSON value could not be converted to System.Int64. " +
                "Path: $.id | LineNumber: 0 | BytePositionInLine: 12."));

        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        var json = await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("BytePositionInLine");
    }

    [Fact]
    public async Task CompleteInvoice_ReturnsCompletedInvoice()
    {
        // Arrange
        var completed = new InvoiceDto { Id = 5, Status = EInvoiceStatus.Completed, DocumentNumber = "FAK2026001" };
        _api.CompleteInvoiceAsync(5, Arg.Any<CancellationToken>()).Returns(completed);

        // Act
        var json = await InvoiceTools.CompleteInvoice(_api, 5);

        // Assert: with JsonStringEnumConverter, status is serialized as a string "Completed"
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("status").GetString().ShouldBe("Completed");
    }

    /// <summary>
    /// #342: TENANT_NOT_READY must reach the MCP client as the structured payload the generic
    /// catch (Exception) below would flatten into a bare error string.
    /// </summary>
    [Fact]
    public async Task CompleteInvoice_TenantNotReady_ReturnsStructuredErrorWithFixRoute()
    {
        // Arrange
        var notReady = new TenantNotReadyApiException(
            "Tenant is not ready. Unresolved blocking issue(s): ISSUER_BANK_ACCOUNT_MISSING.",
            missingFields: ["BankAccount"],
            issues:
            [
                new ReadinessIssueDto
                {
                    Code = "ISSUER_BANK_ACCOUNT_MISSING",
                    Severity = EReadinessSeverity.Blocking,
                    MissingFields = ["BankAccount"],
                    FixRoute = "/my-company"
                }
            ]);
        _api.CompleteInvoiceAsync(5, Arg.Any<CancellationToken>()).ThrowsAsync(notReady);

        // Act
        var json = await InvoiceTools.CompleteInvoice(_api, 5);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TENANT_NOT_READY");
        doc.RootElement.GetProperty("missingFields")[0].GetString().ShouldBe("BankAccount");
        doc.RootElement.GetProperty("issues")[0].GetProperty("fixRoute").GetString().ShouldBe("/my-company");
    }

    [Fact]
    public async Task SendInvoiceEmail_ReturnsSuccessMessage()
    {
        // Arrange
        _api.SendInvoiceEmailAsync(3, Arg.Any<SendInvoiceEmailDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        var json = await InvoiceTools.SendInvoiceEmail(_api, 3, "test@example.com", "Subject", "Body");

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("message").GetString().ShouldContain("test@example.com");
    }

    [Fact]
    public async Task DeleteInvoice_ReturnsSuccessMessage()
    {
        // Arrange
        _api.DeleteInvoiceAsync(7, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Act
        var json = await InvoiceTools.DeleteInvoice(_api, 7);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ListInvoices_ReturnsError_OnApiFailure()
    {
        // Arrange: simulate API throwing an exception whose message carries the raw
        // API error body (see FakvioApiClient.EnsureSuccessAsync) — that text must
        // never reach the AI client (issue #279).
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        // Act
        var json = await InvoiceTools.ListInvoices(_api);

        // Assert: should return a sanitized error JSON, not throw and not leak the
        // exception message.
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("Connection refused");
    }

    [Fact]
    public async Task ListInvoices_PropagatesCancellation_WhenTheCallerCancelled()
    {
        // A request the caller cancelled is not a domain error — the tool must let it
        // bubble out instead of turning it into a fake "error" JSON result (issue #279).
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => InvoiceTools.ListInvoices(_api, ct: cts.Token));
    }

    [Fact]
    public async Task ListInvoices_ReturnsSanitizedError_OnHttpClientTimeout()
    {
        // HttpClient throws TaskCanceledException (a subclass of OperationCanceledException)
        // on its OWN timeout, and then the caller's token was never cancelled. That is an
        // API-side failure, not a cancellation, so it has to come back as sanitized JSON:
        // an MCP tool that throws kills the whole call (issue #279).
        using var cts = new CancellationTokenSource();
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Throws(new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException()));

        var json = await InvoiceTools.ListInvoices(_api, ct: cts.Token);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("HttpClient.Timeout");
    }

    // ── ExportInvoicePdf tests ──────────────────────────────────────────

    [Fact]
    public async Task ExportInvoicePdf_ReturnsBase64Content()
    {
        // Arrange — invoice exists and PDF bytes are returned
        var invoice = new InvoiceDto
        {
            Id = 42, DocumentNumber = "FV-2024-0001",
            DocumentType = EDocumentType.Invoice
        };
        _api.GetInvoiceByIdAsync(42, Arg.Any<CancellationToken>()).Returns(invoice);
        _api.ExportInvoicePdfAsync(42, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x25, 0x50, 0x44, 0x46 }); // %PDF header

        // Act
        var json = await InvoiceTools.ExportInvoicePdf(_api, 42);

        // Assert — should contain base64 content and correct file name
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("fileName").GetString().ShouldBe("Invoice_FV-2024-0001.pdf");
        doc.RootElement.GetProperty("mimeType").GetString().ShouldBe("application/pdf");
        doc.RootElement.GetProperty("base64Content").GetString().ShouldNotBeNullOrEmpty();
        doc.RootElement.GetProperty("sizeBytes").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task ExportInvoicePdf_CreditNote_UsesCorrectPrefix()
    {
        // Arrange
        var invoice = new InvoiceDto
        {
            Id = 10, DocumentNumber = "DP-2024-0001",
            DocumentType = EDocumentType.CreditNote
        };
        _api.GetInvoiceByIdAsync(10, Arg.Any<CancellationToken>()).Returns(invoice);
        _api.ExportInvoicePdfAsync(10, Arg.Any<CancellationToken>())
            .Returns(new byte[] { 0x25, 0x50 });

        // Act
        var json = await InvoiceTools.ExportInvoicePdf(_api, 10);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("fileName").GetString().ShouldBe("CreditNote_DP-2024-0001.pdf");
    }

    [Fact]
    public async Task ExportInvoicePdf_NotFound_ReturnsError()
    {
        // Arrange
        _api.GetInvoiceByIdAsync(999, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        // Act
        var json = await InvoiceTools.ExportInvoicePdf(_api, 999);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("999");
    }

    // ── ClientTools tests ──────────────────────────────────────────────

    [Fact]
    public async Task ListClients_PassesSearchFilter()
    {
        // Arrange
        _api.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>());

        // Act
        var json = await ClientTools.ListClients(_api, search: "Acme");

        // Assert
        await _api.Received(1).GetClientsPagedAsync(
            Arg.Is<ClientFilterDto>(f => f.Search == "Acme"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetClient_ReturnsError_WhenNotFound()
    {
        // Arrange
        _api.GetClientByIdAsync(999, Arg.Any<CancellationToken>()).Returns((ClientDto?)null);

        // Act
        var json = await ClientTools.GetClient(_api, 999);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("not found");
    }

    [Fact]
    public async Task CreateClient_CreatesFromTypedDto()
    {
        // Arrange
        var created = new ClientDto { Id = 5, CompanyName = "Acme s.r.o." };
        _api.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(created);

        // Act — N2.5: typed DTO parameter, no more "JSON string of CreateClientDto".
        var json = await ClientTools.CreateClient(_api,
            new CreateClientDto { CompanyName = "Acme s.r.o.", RegistrationNumber = "12345678" });

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("companyName").GetString().ShouldBe("Acme s.r.o.");
    }

    [Fact]
    public async Task LookupAres_ReturnsNull_ForInvalidIco()
    {
        // Arrange
        _api.FetchFromAresAsync("invalid", Arg.Any<CancellationToken>()).Returns((ClientDto?)null);

        // Act
        var json = await ClientTools.LookupAres(_api, "invalid");

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("ARES lookup failed");
    }

    [Fact]
    public async Task GetIssuer_ReturnsIssuerData()
    {
        // Arrange
        var issuer = new ClientDto { Id = 1, CompanyName = "My Company", IsIssuer = true };
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(issuer);

        // Act
        var json = await ClientTools.GetIssuer(_api);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("companyName").GetString().ShouldBe("My Company");
        doc.RootElement.GetProperty("isIssuer").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task GetClient_PropagatesCancellation_WhenTheCallerCancelled()
    {
        // ClientTools got its own catch-all copied in by this PR just like every other
        // tool class — verify the cancellation carve-out actually landed here too,
        // not only on the one representative tool (InvoiceTools.ListInvoices) covered above.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _api.GetClientByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => ClientTools.GetClient(_api, 1, ct: cts.Token));
    }

    [Fact]
    public async Task GetClient_ReturnsSanitizedError_OnApiFailure()
    {
        // Same leak this whole issue is about: an exception message that could carry the
        // raw API error body must never reach the AI client via ClientTools either.
        _api.GetClientByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        var json = await ClientTools.GetClient(_api, 1);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("Connection refused");
    }

    // ── TemplateTools tests ────────────────────────────────────────────

    [Fact]
    public async Task ListTemplates_ReturnsTemplateList()
    {
        // Arrange
        var templates = new List<InvoiceTemplateDto>
        {
            new() { Id = 1, Name = "Monthly Hosting" },
            new() { Id = 2, Name = "One-time Project" }
        };
        _api.GetActiveTemplatesAsync(null, Arg.Any<CancellationToken>()).Returns(templates);

        // Act
        var json = await TemplateTools.ListTemplates(_api);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task CreateInvoiceFromTemplate_PassesCorrectTemplateId()
    {
        // Arrange
        var created = new InvoiceDto { Id = 20, Status = EInvoiceStatus.Draft };
        _api.CreateInvoiceFromTemplateAsync(5, Arg.Any<CreateInvoiceFromTemplateDto>(), Arg.Any<CancellationToken>())
            .Returns(created);

        // Act — N2.5: typed DTO parameter, no more "JSON string with creation options".
        var json = await TemplateTools.CreateInvoiceFromTemplate(_api, 5,
            new CreateInvoiceFromTemplateDto { ClientId = 10, AutoComplete = false });

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(20);
    }

    /// <summary>Only reachable with autoComplete = true (#342) — same structured payload as CompleteInvoice.</summary>
    [Fact]
    public async Task CreateInvoiceFromTemplate_TenantNotReady_ReturnsStructuredErrorWithFixRoute()
    {
        // Arrange
        var notReady = new TenantNotReadyApiException(
            "Tenant is not ready. Unresolved blocking issue(s): ISSUER_BANK_ACCOUNT_MISSING.",
            missingFields: ["BankAccount"],
            issues:
            [
                new ReadinessIssueDto
                {
                    Code = "ISSUER_BANK_ACCOUNT_MISSING",
                    Severity = EReadinessSeverity.Blocking,
                    MissingFields = ["BankAccount"],
                    FixRoute = "/my-company"
                }
            ]);
        _api.CreateInvoiceFromTemplateAsync(5, Arg.Any<CreateInvoiceFromTemplateDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(notReady);

        // Act
        var json = await TemplateTools.CreateInvoiceFromTemplate(_api, 5,
            new CreateInvoiceFromTemplateDto { ClientId = 10, AutoComplete = true });

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TENANT_NOT_READY");
        doc.RootElement.GetProperty("issues")[0].GetProperty("fixRoute").GetString().ShouldBe("/my-company");
    }

    [Fact]
    public async Task GetTemplate_PropagatesCancellation_WhenTheCallerCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _api.GetTemplateByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => TemplateTools.GetTemplate(_api, 1, ct: cts.Token));
    }

    [Fact]
    public async Task GetTemplate_ReturnsSanitizedError_OnApiFailure()
    {
        _api.GetTemplateByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        var json = await TemplateTools.GetTemplate(_api, 1);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("Connection refused");
    }

    // ── ReportingTools tests ───────────────────────────────────────────

    [Fact]
    public async Task GetDashboard_ReturnsDashboardJson()
    {
        // Arrange
        var dashboard = new DashboardDto
        {
            InvoicesDueThisMonthCount = 10,
            InvoicesDueThisMonthTotalWithVat = 120_000m,
            TotalClients = 25,
            UnpaidAmount = 50000m,
            OverdueInvoicesCount = 3
        };
        _api.GetDashboardAsync(Arg.Any<CancellationToken>()).Returns(dashboard);

        // Act
        var json = await ReportingTools.GetDashboard(_api);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("invoicesDueThisMonthCount").GetInt32().ShouldBe(10);
        doc.RootElement.GetProperty("overdueInvoicesCount").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task GetDashboard_PropagatesCancellation_WhenTheCallerCancelled()
    {
        // ReportingTools had 6 catch-alls rewritten by this PR and none of them were
        // exercised for cancellation or sanitized errors before — pin down one
        // representative method so a future edit here cannot silently reintroduce
        // the leak or the swallowed cancellation.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _api.GetDashboardAsync(Arg.Any<CancellationToken>()).Throws(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(
            () => ReportingTools.GetDashboard(_api, ct: cts.Token));
    }

    [Fact]
    public async Task GetDashboard_ReturnsSanitizedError_OnApiFailure()
    {
        _api.GetDashboardAsync(Arg.Any<CancellationToken>()).Throws(new HttpRequestException("Connection refused"));

        var json = await ReportingTools.GetDashboard(_api);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldBe("internal_error");
        doc.RootElement.GetProperty("message").GetString().ShouldNotContain("Connection refused");
    }

    [Fact]
    public async Task GetOverdueInvoices_SetsCorrectFilters()
    {
        // Arrange
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        // Act
        await ReportingTools.GetOverdueInvoices(_api);

        // Assert: verify filter is set for overdue completed invoices
        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f =>
                f.Status == EInvoiceStatus.Completed &&
                f.IsOverdue == true &&
                f.SortBy == "DueDate" &&
                f.SortDirection == "asc"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetClientInvoices_FiltersForSpecificClient()
    {
        // Arrange
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        // Act
        await ReportingTools.GetClientInvoices(_api, clientId: 42);

        // Assert
        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f => f.ClientId == 42),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInvoicesByDateRange_ParsesDatesCorrectly()
    {
        // Arrange
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<InvoiceDto>());

        // Act
        await ReportingTools.GetInvoicesByDateRange(_api, "2026-01-01", "2026-01-31");

        // Assert
        await _api.Received(1).GetInvoicesPagedAsync(
            Arg.Is<InvoiceFilterDto>(f =>
                f.IssueDateFrom == new DateTime(2026, 1, 1) &&
                f.IssueDateTo == new DateTime(2026, 1, 31)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInvoicesByDateRange_ReturnsError_OnInvalidDate()
    {
        // Act
        var json = await ReportingTools.GetInvoicesByDateRange(_api, "not-a-date", "2026-01-31");

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Invalid dateFrom");
    }
}

public class InvoiceBankAccountToolsTests
{
    private readonly IFakvioApiClient _api = Substitute.For<IFakvioApiClient>();

    [Fact]
    public async Task CreateInvoice_PassesBankAccountIdToApi()
    {
        _api.CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns(new InvoiceDto { Id = 1 });
        _api.GetIssuerAsync(Arg.Any<CancellationToken>()).Returns(new ClientDto { Id = 2, IsVatPayer = false });
        _api.GetActiveCurrenciesAsync(Arg.Any<CancellationToken>()).Returns(new List<CurrencyDto> { new() { Id = 1, Code = "CZK" } });
        var items = new List<CreateInvoiceItemDto> { new() { Description = "X", Quantity = 1, UnitPrice = 1 } };

        await InvoiceTools.CreateInvoice(_api, clientId: 1, items: items, bankAccountId: 77);

        await _api.Received(1).CreateInvoiceAsync(
            Arg.Is<CreateInvoiceDto>(d => d.BankAccountId == 77), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetInvoiceBankAccount_SendsUpdateWithBankAccountId()
    {
        _api.UpdateInvoiceAsync(5, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(new InvoiceDto { Id = 5, BankAccountNumber = "222/0100" });

        var json = await InvoiceTools.SetInvoiceBankAccount(_api, invoiceId: 5, bankAccountId: 11);

        JsonDocument.Parse(json).RootElement.GetProperty("bankAccountNumber").GetString().ShouldBe("222/0100");
        await _api.Received(1).UpdateInvoiceAsync(
            5, Arg.Is<UpdateInvoiceDto>(d => d.BankAccountId == 11), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetInvoiceBankAccount_NotFound_ReturnsError()
    {
        _api.UpdateInvoiceAsync(9, Arg.Any<UpdateInvoiceDto>(), Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        var json = await InvoiceTools.SetInvoiceBankAccount(_api, invoiceId: 9, bankAccountId: 1);

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString().ShouldContain("not found");
    }
}
