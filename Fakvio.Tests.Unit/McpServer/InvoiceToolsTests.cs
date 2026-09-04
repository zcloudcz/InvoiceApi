using System.Text.Json;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Email;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
using Fakvio.Contracts.Dto.Readiness;
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

        var invoiceJson = JsonSerializer.Serialize(new
        {
            documentType = "Invoice",
            clientId = 1,
            issuerId = 2,
            currencyId = 1,
            invoiceItem = new[]
            {
                new { description = "Test", quantity = 1, unitPrice = 100, vatRatePercentage = 21 }
            }
        });

        // Act
        var json = await InvoiceTools.CreateInvoice(_api, invoiceJson);

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetInt64().ShouldBe(10);
        await _api.Received(1).CreateInvoiceAsync(Arg.Any<CreateInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateInvoice_ReturnsError_OnInvalidJson()
    {
        // Act: pass invalid JSON
        var json = await InvoiceTools.CreateInvoice(_api, "this is not json");

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Invalid JSON");
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
        // Arrange: simulate API throwing an exception
        _api.GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Throws(new HttpRequestException("Connection refused"));

        // Act
        var json = await InvoiceTools.ListInvoices(_api);

        // Assert: should return error JSON, not throw
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("error").GetString().ShouldContain("Connection refused");
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
    public async Task CreateClient_DeserializesAndCreates()
    {
        // Arrange
        var created = new ClientDto { Id = 5, CompanyName = "Acme s.r.o." };
        _api.CreateClientAsync(Arg.Any<CreateClientDto>(), Arg.Any<CancellationToken>())
            .Returns(created);

        // Act
        var json = await ClientTools.CreateClient(_api,
            "{\"companyName\":\"Acme s.r.o.\",\"registrationNumber\":\"12345678\"}");

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

        // Act
        var json = await TemplateTools.CreateInvoiceFromTemplate(_api, 5,
            "{\"clientId\":10,\"autoComplete\":false}");

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
            "{\"clientId\":10,\"autoComplete\":true}");

        // Assert
        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().ShouldBe("TENANT_NOT_READY");
        doc.RootElement.GetProperty("issues")[0].GetProperty("fixRoute").GetString().ShouldBe("/my-company");
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
