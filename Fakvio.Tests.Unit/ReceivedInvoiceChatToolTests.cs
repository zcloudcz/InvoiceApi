using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the three received-invoice chat tools:
///   - GetReceivedInvoiceTool
///   - ListReceivedInvoicesTool
///   - SearchReceivedInvoicesTool
///
/// Uses NSubstitute mocks for IReceivedInvoiceService and IClientService.
/// Does NOT touch the database — verifies tool dispatch, parameter parsing,
/// output formatting, and error handling.
/// </summary>
public class ReceivedInvoiceChatToolTests
{
    // ─── Shared helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal ReceivedInvoiceDto for use in test scenarios.
    /// </summary>
    private static ReceivedInvoiceDto BuildInvoiceDto(
        long id = 1,
        string docNumber = "INV-2024-001",
        string supplierName = "Alza.cz",
        decimal totalWithVat = 12100m,
        EReceivedInvoiceStatus status = EReceivedInvoiceStatus.Received)
    {
        return new ReceivedInvoiceDto
        {
            Id = id,
            DocumentNumber = docNumber,
            SupplierName = supplierName,
            SupplierId = 42,
            Status = status,
            IssueDate = new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2024, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            TotalBeforeVat = 10000m,
            TotalVat = 2100m,
            TotalWithVat = totalWithVat,
            CurrencyCode = "CZK",
            CurrencySymbol = "Kč",
            Items = new List<ReceivedInvoiceItemDto>
            {
                new()
                {
                    Id = 10, OrderIndex = 0, Description = "Laptop",
                    Quantity = 1, Unit = "ks", UnitPrice = 10000m,
                    VatRatePercentage = 21m,
                    TotalBeforeVat = 10000m, VatAmount = 2100m, TotalWithVat = 12100m
                }
            },
            CreatedAt = new DateTime(2024, 4, 1, 12, 0, 0, DateTimeKind.Utc)
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GetReceivedInvoiceTool
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GetReceivedInvoiceTool — returns failure when neither id nor document_number is provided.
    /// </summary>
    [Fact]
    public async Task GetReceivedInvoiceTool_NoParameters_ReturnsFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var tool = new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("at least one");
    }

    /// <summary>
    /// GetReceivedInvoiceTool — resolves invoice by numeric id and returns formatted text.
    /// </summary>
    [Fact]
    public async Task GetReceivedInvoiceTool_ValidId_ReturnsFormattedInvoice()
    {
        var invoice = BuildInvoiceDto(id: 7, docNumber: "267708922");
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(invoice);

        var tool = new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>());
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "7" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("ID: 7");
        result.OutputText.ShouldContain("267708922");
        result.OutputText.ShouldContain("Alza.cz");
        // TotalWithVat formatting depends on the test runner's culture (e.g. "12,100.00" vs "12 100,00").
        // Check for the currency code instead — always present regardless of culture.
        result.OutputText.ShouldContain("CZK");
        result.OutputText.ShouldContain("Laptop");  // Item description
    }

    /// <summary>
    /// GetReceivedInvoiceTool — returns failure when the requested ID does not exist.
    /// </summary>
    [Fact]
    public async Task GetReceivedInvoiceTool_IdNotFound_ReturnsFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((ReceivedInvoiceDto?)null);

        var tool = new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>());
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "999" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("999");
        result.ErrorMessage.ShouldContain("not found");
    }

    /// <summary>
    /// GetReceivedInvoiceTool — resolves invoice by document_number (contains match).
    /// </summary>
    [Fact]
    public async Task GetReceivedInvoiceTool_ValidDocumentNumber_ReturnsFormattedInvoice()
    {
        var invoice = BuildInvoiceDto(id: 5, docNumber: "267708922");
        var service = Substitute.For<IReceivedInvoiceService>();
        // GetByIdAsync not called — document_number path uses GetAllAsync.
        service.GetAllAsync(ct: Arg.Any<CancellationToken>()).Returns(new List<ReceivedInvoiceDto> { invoice });

        var tool = new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>());
        var result = await tool.ExecuteAsync(
            new Dictionary<string, string> { ["document_number"] = "267708922" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("267708922");
        result.OutputText.ShouldContain("Alza.cz");
    }

    /// <summary>
    /// GetReceivedInvoiceTool — returns failure when document_number yields no match.
    /// </summary>
    [Fact]
    public async Task GetReceivedInvoiceTool_DocNumberNotFound_ReturnsFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetAllAsync(ct: Arg.Any<CancellationToken>()).Returns(new List<ReceivedInvoiceDto>());

        var tool = new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>());
        var result = await tool.ExecuteAsync(
            new Dictionary<string, string> { ["document_number"] = "NONEXISTENT" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("NONEXISTENT");
    }

    /// <summary>
    /// GetReceivedInvoiceTool — output includes item-level cross-check section.
    /// </summary>
    [Fact]
    public async Task GetReceivedInvoiceTool_OutputContainsCrossCheck()
    {
        var invoice = BuildInvoiceDto(id: 1);
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(invoice);

        var tool = new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>());
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "1" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("cross-check");
        result.OutputText.ShouldContain("MATCH");
    }

    /// <summary>
    /// GetReceivedInvoiceTool — id=0 or non-numeric falls through to document_number path.
    /// Non-numeric id + no document_number → failure.
    /// </summary>
    [Fact]
    public async Task GetReceivedInvoiceTool_NonNumericId_NoDocNumber_ReturnsFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var tool = new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["id"] = "abc" });

        result.IsSuccess.ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ListReceivedInvoicesTool
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ListReceivedInvoicesTool — returns formatted paged list when no filters applied.
    /// </summary>
    [Fact]
    public async Task ListReceivedInvoicesTool_NoFilters_ReturnsList()
    {
        var inv = BuildInvoiceDto(id: 1, docNumber: "INV-001");
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto> { inv },
            TotalCount = 1, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);

        var clientService = Substitute.For<IClientService>();
        var tool = new ListReceivedInvoicesTool(service, clientService, Substitute.For<ILogger<ListReceivedInvoicesTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("INV-001");
        result.OutputText.ShouldContain("Alza.cz");
        result.OutputText.ShouldContain("total: 1");
    }

    /// <summary>
    /// ListReceivedInvoicesTool — passes status filter to the service.
    /// </summary>
    [Fact]
    public async Task ListReceivedInvoicesTool_StatusFilter_PassedToService()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);

        var clientService = Substitute.For<IClientService>();
        var tool = new ListReceivedInvoicesTool(service, clientService, Substitute.For<ILogger<ListReceivedInvoicesTool>>());

        await tool.ExecuteAsync(new Dictionary<string, string> { ["status"] = "Approved" });

        // Verify service was called with Approved status filter.
        await service.Received(1).GetPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.Status == EReceivedInvoiceStatus.Approved),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ListReceivedInvoicesTool — unknown status string is silently ignored (no filter applied).
    /// </summary>
    [Fact]
    public async Task ListReceivedInvoicesTool_InvalidStatus_IgnoresFilter()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);

        var clientService = Substitute.For<IClientService>();
        var tool = new ListReceivedInvoicesTool(service, clientService, Substitute.For<ILogger<ListReceivedInvoicesTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["status"] = "UNKNOWN_STATUS" });

        // Should succeed with no status filter (not failure).
        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).GetPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.Status == null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ListReceivedInvoicesTool — supplier_name returns failure when supplier not found in client DB.
    /// </summary>
    [Fact]
    public async Task ListReceivedInvoicesTool_UnknownSupplierName_ReturnsFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var clientService = Substitute.For<IClientService>();
        clientService.GetAllClientsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns(new List<ClientDto>());

        var tool = new ListReceivedInvoicesTool(service, clientService, Substitute.For<ILogger<ListReceivedInvoicesTool>>());
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["supplier_name"] = "NONEXISTENT" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("NONEXISTENT");
        result.ErrorMessage.ShouldContain("not found");
    }

    /// <summary>
    /// ListReceivedInvoicesTool — page_size is clamped to 50 even if AI provides a larger value.
    /// </summary>
    [Fact]
    public async Task ListReceivedInvoicesTool_PageSizeClamped_To50()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 50
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);

        var clientService = Substitute.For<IClientService>();
        var tool = new ListReceivedInvoicesTool(service, clientService, Substitute.For<ILogger<ListReceivedInvoicesTool>>());

        await tool.ExecuteAsync(new Dictionary<string, string> { ["page_size"] = "999" });

        await service.Received(1).GetPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.PageSize == 50),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ListReceivedInvoicesTool — empty result shows "No records" message without failing.
    /// </summary>
    [Fact]
    public async Task ListReceivedInvoicesTool_EmptyResult_ShowsNoRecordsMessage()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);

        var clientService = Substitute.For<IClientService>();
        var tool = new ListReceivedInvoicesTool(service, clientService, Substitute.For<ILogger<ListReceivedInvoicesTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No records");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  SearchReceivedInvoicesTool
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SearchReceivedInvoicesTool — returns failure when query parameter is missing.
    /// </summary>
    [Fact]
    public async Task SearchReceivedInvoicesTool_MissingQuery_ReturnsFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var tool = new SearchReceivedInvoicesTool(service, Substitute.For<ILogger<SearchReceivedInvoicesTool>>());

        var result = await tool.ExecuteAsync(new Dictionary<string, string>());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("query");
    }

    /// <summary>
    /// SearchReceivedInvoicesTool — forwards query to GetPagedAsync with Search filter.
    /// </summary>
    [Fact]
    public async Task SearchReceivedInvoicesTool_WithQuery_CallsGetPagedWithSearchFilter()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);
        service.GetAllAsync(Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
               .Returns(new List<ReceivedInvoiceDto>());

        var tool = new SearchReceivedInvoicesTool(service, Substitute.For<ILogger<SearchReceivedInvoicesTool>>());
        await tool.ExecuteAsync(new Dictionary<string, string> { ["query"] = "Alza" });

        await service.Received(1).GetPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.Search == "Alza"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// SearchReceivedInvoicesTool — returns "no results" text (not failure) when nothing matches.
    /// </summary>
    [Fact]
    public async Task SearchReceivedInvoicesTool_NoMatch_ReturnsSuccessWithNoResultsText()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);
        service.GetAllAsync(Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
               .Returns(new List<ReceivedInvoiceDto>());

        var tool = new SearchReceivedInvoicesTool(service, Substitute.For<ILogger<SearchReceivedInvoicesTool>>());
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["query"] = "nonexistent" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No received invoices found");
    }

    /// <summary>
    /// SearchReceivedInvoicesTool — single result includes expanded item detail.
    /// </summary>
    [Fact]
    public async Task SearchReceivedInvoicesTool_SingleMatch_IncludesItemDetail()
    {
        var inv = BuildInvoiceDto(id: 3, docNumber: "267708922");
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto> { inv },
            TotalCount = 1, PageNumber = 1, PageSize = 10
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);
        service.GetAllAsync(Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
               .Returns(new List<ReceivedInvoiceDto>());

        var tool = new SearchReceivedInvoicesTool(service, Substitute.For<ILogger<SearchReceivedInvoicesTool>>());
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["query"] = "267708922" });

        result.IsSuccess.ShouldBeTrue();
        // Expanded single-match detail must contain item info.
        result.OutputText.ShouldContain("Full detail");
        result.OutputText.ShouldContain("Laptop");
    }

    /// <summary>
    /// SearchReceivedInvoicesTool — numeric query also triggers amount-based match via GetAllAsync.
    /// </summary>
    [Fact]
    public async Task SearchReceivedInvoicesTool_NumericQuery_AlsoTriesAmountMatch()
    {
        // Service returns empty page (no text match) …
        var emptyPage = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 10
        };
        // … but GetAllAsync returns an invoice with matching TotalWithVat.
        var amountMatch = BuildInvoiceDto(id: 9, docNumber: "AMT-001", totalWithVat: 12100m);

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(emptyPage);
        service.GetAllAsync(Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
               .Returns(new List<ReceivedInvoiceDto> { amountMatch });

        var tool = new SearchReceivedInvoicesTool(service, Substitute.For<ILogger<SearchReceivedInvoicesTool>>());
        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["query"] = "12100" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("AMT-001");
    }

    /// <summary>
    /// SearchReceivedInvoicesTool — limit parameter is respected and clamped to 50.
    /// </summary>
    [Fact]
    public async Task SearchReceivedInvoicesTool_LimitClamped_To50()
    {
        var paged = new PagedResult<ReceivedInvoiceDto>
        {
            Items = new List<ReceivedInvoiceDto>(), TotalCount = 0, PageNumber = 1, PageSize = 50
        };

        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetPagedAsync(Arg.Any<ReceivedInvoiceFilterDto>(), Arg.Any<CancellationToken>())
               .Returns(paged);
        service.GetAllAsync(Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
               .Returns(new List<ReceivedInvoiceDto>());

        var tool = new SearchReceivedInvoicesTool(service, Substitute.For<ILogger<SearchReceivedInvoicesTool>>());
        await tool.ExecuteAsync(new Dictionary<string, string> { ["query"] = "test", ["limit"] = "999" });

        await service.Received(1).GetPagedAsync(
            Arg.Is<ReceivedInvoiceFilterDto>(f => f.PageSize == 50),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  DetectToolIntent — received invoice regex path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ChatToolExecutor.DetectToolIntent returns true for received-invoice keywords.
    /// These messages must trigger the intent detection so the tool instructions
    /// are appended to the system prompt for the first AI pass.
    /// </summary>
    [Theory]
    [InlineData("přijatá faktura 267708922")]
    [InlineData("Ukaž mi přijatou fakturu č. 2024-0051")]
    [InlineData("received invoice 12345")]
    [InlineData("supplier invoice from Alza")]
    [InlineData("Přijaté faktury od dodavatele")]
    [InlineData("faktura 267708922")]
    [InlineData("incoming invoice 99887766")]
    public void DetectToolIntent_ReturnsTrue_ForReceivedInvoiceKeywords(string message)
    {
        var logger = Substitute.For<ILogger<Infrastructure.Service.ChatTools.ChatToolExecutor>>();

        // Minimal tool list — we only need DetectToolIntent to work.
        var mockTool = Substitute.For<IChatTool>();
        mockTool.ToolName.Returns("get_received_invoice");
        mockTool.Description.Returns("Get received invoice");
        mockTool.ParameterDescription.Returns("id or document_number");

        var executor = new Infrastructure.Service.ChatTools.ChatToolExecutor(
            new List<IChatTool> { mockTool }, logger);

        executor.DetectToolIntent(message).ShouldBeTrue();
    }
}
