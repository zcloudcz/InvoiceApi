using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Dashboard;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.VatReport;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the three read-only reporting chat tools (issue #228):
///   - GetDashboardTool
///   - ListInvoicesTool
///   - GetVatReportTool
///
/// Everything is mocked with NSubstitute — no database, no AI provider. What is verified is
/// what the tools own: parameter parsing, the reporting defaults they apply to the filter,
/// the error paths, and that the formatted output actually carries the numbers the model
/// will be asked about.
///
/// Junior note: assertions avoid comparing formatted amounts. <c>:N2</c> uses the current
/// culture, so "12 100,00" on a Czech machine and "12,100.00" on a build agent are both
/// correct — the tests check the culture-independent parts instead.
/// </summary>
public class ReportingChatToolTests
{
    // ─── Shared helpers ───────────────────────────────────────────────────

    private static InvoiceDto BuildInvoice(
        long id = 1,
        string documentNumber = "FAK-2026-001",
        string clientName = "Alza.cz",
        EInvoiceStatus status = EInvoiceStatus.Completed,
        EDocumentType documentType = EDocumentType.Invoice)
        => new()
        {
            Id = id,
            DocumentNumber = documentNumber,
            ClientName = clientName,
            Status = status,
            DocumentType = documentType,
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            TotalBeforeVat = 10000m,
            TotalVat = 2100m,
            TotalWithVat = 12100m,
            CurrencyCode = "CZK"
        };

    /// <summary>
    /// Builds a ListInvoicesTool over mocks and hands back the mocks so the test can assert
    /// on the filter the tool built. <paramref name="companyId"/> is what the tenant resolver
    /// reports for the signed-in user.
    /// </summary>
    private static (ListInvoicesTool Tool, IInvoiceService Service) CreateListTool(
        PagedResult<InvoiceDto>? page = null,
        long? companyId = 42)
    {
        var service = Substitute.For<IInvoiceService>();
        service
            .GetInvoicesPagedAsync(Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(page ?? new PagedResult<InvoiceDto>([BuildInvoice()], 1, 1, 10));

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(companyId);

        return (new ListInvoicesTool(service, tenantResolver, Substitute.For<ILogger<ListInvoicesTool>>()), service);
    }

    /// <summary>Captures the filter the tool passed to the service.</summary>
    private static InvoiceFilterDto CapturedFilter(IInvoiceService service)
        => (InvoiceFilterDto)service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IInvoiceService.GetInvoicesPagedAsync))
            .GetArguments()[0]!;

    // ═══════════════════════════════════════════════════════════════════════
    //  GetDashboardTool
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The dashboard tool takes no parameters — an empty schema is what tells the model
    /// it can just call it.
    /// </summary>
    [Fact]
    public void GetDashboardTool_DeclaresNoParameters()
    {
        var tool = new GetDashboardTool(
            Substitute.For<IDashboardService>(),
            Substitute.For<ITenantResolver>(),
            Substitute.For<ILogger<GetDashboardTool>>());

        tool.ToolName.ShouldBe("get_dashboard");
        tool.Parameters.ShouldBeEmpty();
    }

    /// <summary>
    /// The dashboard must be scoped to the issuer the user is signed in as — the same
    /// scoping DashboardController applies. Without it a multi-issuer tenant would get
    /// another issuer's numbers reported as its own.
    /// </summary>
    [Fact]
    public async Task GetDashboardTool_ScopesTheQueryToTheSignedInCompany()
    {
        var dashboardService = Substitute.For<IDashboardService>();
        dashboardService.GetDashboardAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DashboardDto());

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(42L);

        var tool = new GetDashboardTool(dashboardService, tenantResolver, Substitute.For<ILogger<GetDashboardTool>>());

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        await dashboardService.Received(1).GetDashboardAsync(42L, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// SysAdmin without impersonation has no company — the tool must pass null through
    /// (tenant-wide view) rather than invent an id.
    /// </summary>
    [Fact]
    public async Task GetDashboardTool_NoCompanyContext_PassesNullThrough()
    {
        var dashboardService = Substitute.For<IDashboardService>();
        dashboardService.GetDashboardAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DashboardDto());

        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns((long?)null);

        var tool = new GetDashboardTool(dashboardService, tenantResolver, Substitute.For<ILogger<GetDashboardTool>>());

        await tool.ExecuteAsync([]);

        await dashboardService.Received(1).GetDashboardAsync(null, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The output has to carry every headline figure — this text IS the model's only source
    /// for the answer, so a dropped field becomes a hallucinated one.
    /// </summary>
    [Fact]
    public async Task GetDashboardTool_FormatsEveryHeadlineFigure()
    {
        var dashboard = new DashboardDto
        {
            InvoicesDueThisMonthCount = 3,
            InvoicesDueThisMonthTotalWithoutVat = 30000m,
            InvoicesDueThisMonthTotalWithVat = 36300m,
            TotalClients = 7,
            UnpaidAmount = 99000m,
            OverdueInvoicesCount = 2,
            RecentInvoices = [BuildInvoice(id: 5, documentNumber: "FAK-2026-005")],
            OverdueInvoices = [BuildInvoice(id: 9, documentNumber: "FAK-2025-099", clientName: "Pozdní s.r.o.")],
            InvoiceCountByStatus = new Dictionary<string, int> { ["Completed"] = 12 },
            InvoiceTotalByClient = new Dictionary<string, decimal> { ["Alza.cz"] = 250000m }
        };

        var dashboardService = Substitute.For<IDashboardService>();
        dashboardService.GetDashboardAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>()).Returns(dashboard);

        var tool = new GetDashboardTool(
            dashboardService, Substitute.For<ITenantResolver>(), Substitute.For<ILogger<GetDashboardTool>>());

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Invoices due this month: 3");
        result.OutputText.ShouldContain("Active clients: 7");
        result.OutputText.ShouldContain("Overdue invoices: 2");
        result.OutputText.ShouldContain("FAK-2026-005");
        result.OutputText.ShouldContain("FAK-2025-099");
        result.OutputText.ShouldContain("Pozdní s.r.o.");
        result.OutputText.ShouldContain("Completed: 12");
        result.OutputText.ShouldContain("Alza.cz");
    }

    /// <summary>
    /// An empty tenant must not render empty headings — a heading with nothing under it is
    /// an invitation for the model to fill the gap.
    /// </summary>
    [Fact]
    public async Task GetDashboardTool_EmptyTenant_OmitsEmptySections()
    {
        var dashboardService = Substitute.For<IDashboardService>();
        dashboardService.GetDashboardAsync(Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(new DashboardDto());

        var tool = new GetDashboardTool(
            dashboardService, Substitute.For<ITenantResolver>(), Substitute.For<ILogger<GetDashboardTool>>());

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Active clients: 0");
        result.OutputText.ShouldNotContain("Recent invoices:");
        result.OutputText.ShouldNotContain("Top clients by revenue:");
        result.OutputText.ShouldNotContain("Invoice count by status:");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ListInvoicesTool
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// No parameters at all is a valid call — the model uses it for "ukaž mi faktury".
    /// Defaults: first page, ten rows, scoped to the signed-in issuer.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_NoParameters_UsesDefaultsAndScopesToCompany()
    {
        var (tool, service) = CreateListTool();

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();

        var filter = CapturedFilter(service);
        filter.Page.ShouldBe(1);
        filter.PageSize.ShouldBe(10);
        filter.IssuerId.ShouldBe(42);
        filter.Status.ShouldBeNull();
        filter.IsOverdue.ShouldBeNull();
    }

    /// <summary>
    /// Every scalar filter has to reach the DTO — this is the parameter-parsing contract
    /// the issue asks to be covered.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_TranslatesEveryFilterParameter()
    {
        var (tool, service) = CreateListTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["page"] = "2",
            ["page_size"] = "25",
            ["status"] = "paid",                 // lowercase on purpose — models are inconsistent
            ["document_type"] = "CreditNote",
            ["client_name"] = "  Alza  ",
            ["issue_date_from"] = "2026-01-01",
            ["issue_date_to"] = "2026-03-31"
        });

        result.IsSuccess.ShouldBeTrue();

        var filter = CapturedFilter(service);
        filter.Page.ShouldBe(2);
        filter.PageSize.ShouldBe(25);
        filter.Status.ShouldBe(EInvoiceStatus.Paid);
        filter.DocumentType.ShouldBe(EDocumentType.CreditNote);
        filter.ClientName.ShouldBe("Alza");
        filter.IssueDateFrom.ShouldBe(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        filter.IssueDateTo.ShouldBe(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    /// <summary>
    /// Npgsql rejects Unspecified DateTimeKind against 'timestamp with time zone' columns,
    /// so a date that parses but carries the wrong Kind blows up only in production.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_ParsedDates_AreUtc()
    {
        var (tool, service) = CreateListTool();

        await tool.ExecuteAsync(new Dictionary<string, string> { ["issue_date_from"] = "15.03.2026" });

        var filter = CapturedFilter(service);
        filter.IssueDateFrom.ShouldBe(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));
        filter.IssueDateFrom!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    /// <summary>
    /// A date the tool cannot parse must fail loudly, exactly like GetVatReportTool does.
    /// Dropping the filter instead would answer a different question than the one asked:
    /// "kolik jsme vystavili za březen" would silently become "za celou historii" and the
    /// model would report that number as the March total.
    /// </summary>
    [Theory]
    [InlineData("issue_date_from")]
    [InlineData("issue_date_to")]
    public async Task ListInvoicesTool_UnparsableDate_FailsWithoutQueryingTheService(string brokenParameter)
    {
        var (tool, service) = CreateListTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { [brokenParameter] = "2026-03" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain(brokenParameter);
        result.ErrorMessage!.ShouldContain("YYYY-MM-DD");
        await service.DidNotReceive().GetInvoicesPagedAsync(
            Arg.Any<InvoiceFilterDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A missing or blank date is not an error — these filters are optional, and "no value"
    /// legitimately means "no date restriction". Only a present, unreadable value fails.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_BlankDate_IsTreatedAsNoFilter()
    {
        var (tool, service) = CreateListTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["issue_date_from"] = "  " });

        result.IsSuccess.ShouldBeTrue();
        CapturedFilter(service).IssueDateFrom.ShouldBeNull();
    }

    /// <summary>
    /// The overdue cut must match the dashboard's definition (Completed + past due) and sort
    /// the most overdue first. Without the status default, drafts with an old due date would
    /// be reported as arrears.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_Overdue_DefaultsToCompletedAndSortsByDueDate()
    {
        var (tool, service) = CreateListTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["overdue"] = "true" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Overdue issued invoices");

        var filter = CapturedFilter(service);
        filter.IsOverdue.ShouldBe(true);
        filter.Status.ShouldBe(EInvoiceStatus.Completed);
        filter.SortBy.ShouldBe("DueDate");
        filter.SortDirection.ShouldBe("asc");
    }

    /// <summary>
    /// The Completed default must not overwrite an explicit status — that is how the model
    /// asks for partially paid arrears, which the single-status filter cannot express otherwise.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_Overdue_KeepsAnExplicitStatus()
    {
        var (tool, service) = CreateListTool();

        await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["overdue"] = "true",
            ["status"] = "PartiallyPaid"
        });

        var filter = CapturedFilter(service);
        filter.IsOverdue.ShouldBe(true);
        filter.Status.ShouldBe(EInvoiceStatus.PartiallyPaid);
    }

    /// <summary>
    /// ChatToolExecutor validates the TRIMMED value but dispatches the raw one, so " true "
    /// reaches the tool as-is. Comparing it untrimmed would pass validation and then silently
    /// turn the arrears question into "list everything" — the worst kind of wrong answer,
    /// because the header still says invoices and the model presents them as receivables.
    /// </summary>
    [Theory]
    [InlineData(" true ")]
    [InlineData("TRUE\t")]
    public async Task ListInvoicesTool_Overdue_IgnoresSurroundingWhitespace(string rawValue)
    {
        var (tool, service) = CreateListTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["overdue"] = rawValue });

        result.OutputText.ShouldContain("Overdue issued invoices");

        var filter = CapturedFilter(service);
        filter.IsOverdue.ShouldBe(true);
        filter.Status.ShouldBe(EInvoiceStatus.Completed);
    }

    /// <summary>
    /// overdue = false must stay a plain listing — no hidden status filter, no re-sorting.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_OverdueFalse_AppliesNoReportingDefaults()
    {
        var (tool, service) = CreateListTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string> { ["overdue"] = "false" });

        result.OutputText.ShouldContain("Issued invoices");
        result.OutputText.ShouldNotContain("Overdue issued invoices");

        var filter = CapturedFilter(service);
        filter.IsOverdue.ShouldBeNull();
        filter.Status.ShouldBeNull();
        filter.SortBy.ShouldBeNull();
    }

    /// <summary>
    /// page_size is capped so one chat answer cannot drag hundreds of rows into the model's
    /// context, and a nonsensical page number falls back to the first page instead of throwing.
    /// </summary>
    [Theory]
    [InlineData("500", 50)]
    [InlineData("0", 10)]
    [InlineData("-3", 10)]
    public async Task ListInvoicesTool_PageSize_IsClampedToSaneValues(string requested, int expected)
    {
        var (tool, service) = CreateListTool();

        await tool.ExecuteAsync(new Dictionary<string, string> { ["page_size"] = requested, ["page"] = "0" });

        var filter = CapturedFilter(service);
        filter.PageSize.ShouldBe(expected);
        filter.Page.ShouldBe(1);
    }

    /// <summary>
    /// An empty result is reported as such. Returning nothing at all would let the model
    /// answer from its own imagination.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_NoMatches_SaysSoExplicitly()
    {
        var (tool, _) = CreateListTool(page: new PagedResult<InvoiceDto>([], 0, 1, 10));

        var result = await tool.ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No records match");
    }

    /// <summary>
    /// The rows have to identify each document well enough for a follow-up question
    /// ("stáhni tu poslední") to resolve it.
    /// </summary>
    [Fact]
    public async Task ListInvoicesTool_FormatsOneRowPerDocument()
    {
        var page = new PagedResult<InvoiceDto>(
            [
                BuildInvoice(id: 1, documentNumber: "FAK-2026-001"),
                BuildInvoice(id: 2, documentNumber: "DOB-2026-002", documentType: EDocumentType.CreditNote)
            ],
            totalCount: 2, pageNumber: 1, pageSize: 10);

        var (tool, _) = CreateListTool(page);

        var result = await tool.ExecuteAsync([]);

        result.OutputText.ShouldContain("total: 2");
        result.OutputText.ShouldContain("ID=1");
        result.OutputText.ShouldContain("FAK-2026-001");
        result.OutputText.ShouldContain("ID=2");
        result.OutputText.ShouldContain("CreditNote");
        result.OutputText.ShouldContain("2026-03-15");   // due date, ISO regardless of culture
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  GetVatReportTool
    // ═══════════════════════════════════════════════════════════════════════

    private static (GetVatReportTool Tool, IVatReportService Service) CreateVatTool(VatReportDto? report = null)
    {
        var service = Substitute.For<IVatReportService>();
        service.GetReportAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(report ?? new VatReportDto());

        return (new GetVatReportTool(service, Substitute.For<ILogger<GetVatReportTool>>()), service);
    }

    /// <summary>
    /// Both dates reach the service as UTC — see the Npgsql note on the ListInvoicesTool test.
    /// </summary>
    [Fact]
    public async Task GetVatReportTool_ValidPeriod_CallsTheServiceWithUtcDates()
    {
        var (tool, service) = CreateVatTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["date_from"] = "2026-01-01",
            ["date_to"] = "2026-03-31"
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).GetReportAsync(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Czech date input is accepted — users dictate "1.1.2026" and the model forwards it.
    /// </summary>
    [Fact]
    public async Task GetVatReportTool_AcceptsCzechDateFormat()
    {
        var (tool, service) = CreateVatTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["date_from"] = "01.01.2026",
            ["date_to"] = "31.03.2026"
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).GetReportAsync(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An unparsable date must fail loudly. A VAT report for a silently guessed period looks
    /// exactly as plausible as the right one, and the user would file it.
    /// </summary>
    [Theory]
    [InlineData("date_from")]
    [InlineData("date_to")]
    public async Task GetVatReportTool_UnparsableDate_FailsWithoutQueryingTheService(string brokenParameter)
    {
        var (tool, service) = CreateVatTool();

        var parameters = new Dictionary<string, string>
        {
            ["date_from"] = "2026-01-01",
            ["date_to"] = "2026-03-31"
        };
        parameters[brokenParameter] = "first quarter";

        var result = await tool.ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain(brokenParameter);
        result.ErrorMessage!.ShouldContain("YYYY-MM-DD");
        await service.DidNotReceive().GetReportAsync(
            Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A reversed period would silently return an empty report that reads like "no VAT to pay".
    /// </summary>
    [Fact]
    public async Task GetVatReportTool_ReversedPeriod_FailsWithoutQueryingTheService()
    {
        var (tool, service) = CreateVatTool();

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["date_from"] = "2026-03-31",
            ["date_to"] = "2026-01-01"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("2026-03-31");
        result.ErrorMessage!.ShouldContain("2026-01-01");
        await service.DidNotReceive().GetReportAsync(
            Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Liability and refund are opposite signs of one number; the model must not have to
    /// work out which one a negative value means.
    /// </summary>
    [Fact]
    public async Task GetVatReportTool_PositiveLiability_IsLabelledAsAmountToPay()
    {
        var (tool, _) = CreateVatTool(new VatReportDto
        {
            PeriodFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            PeriodTo = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc),
            TotalOutputVat = 21000m,
            TotalInputVat = 6000m,
            TaxLiability = 15000m,
            IssuedInvoiceCount = 4,
            ReceivedInvoiceCount = 2,
            OutputVat = [new VatReportLineDto { VatRatePercentage = 21m, VatRateLabel = "DPH 21%", BaseAmount = 100000m, VatAmount = 21000m, ItemCount = 4 }]
        });

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["date_from"] = "2026-01-01",
            ["date_to"] = "2026-03-31"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("2026-01-01");
        result.OutputText.ShouldContain("2026-03-31");
        result.OutputText.ShouldContain("Tax liability (to pay)");
        result.OutputText.ShouldNotContain("Excess deduction");
        result.OutputText.ShouldContain("4 issued, 2 received");
        result.OutputText.ShouldContain("Output VAT by rate");
        result.OutputText.ShouldContain("DPH 21%");
    }

    /// <summary>
    /// The refund case: a negative liability is reported as a refund, and as a positive number —
    /// "refund: -15000" is exactly the kind of double negative that gets misread.
    /// </summary>
    [Fact]
    public async Task GetVatReportTool_NegativeLiability_IsLabelledAsRefund()
    {
        var (tool, _) = CreateVatTool(new VatReportDto { TaxLiability = -15000m });

        var result = await tool.ExecuteAsync(new Dictionary<string, string>
        {
            ["date_from"] = "2026-01-01",
            ["date_to"] = "2026-03-31"
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Excess deduction (refund)");
        result.OutputText.ShouldNotContain("Tax liability (to pay)");
        result.OutputText.ShouldNotContain("-15");
    }
}
