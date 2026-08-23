using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Chat;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the four WRITE chat tools over received invoices (issue #218):
///   - create_received_invoice
///   - approve_received_invoice
///   - mark_received_invoice_paid
///   - delete_received_invoice  (behind the confirm gate from #212)
///
/// The read-only trio lives in <see cref="ReceivedInvoiceChatToolTests"/>; these tools are kept
/// apart because what matters here is different: not the formatting of an answer, but that the
/// right record is changed — and that nothing is changed when the tool should refuse.
///
/// Junior note: every dependency is an NSubstitute mock, so no database is touched. The
/// assertions that a service method was NOT called (<c>DidNotReceive</c>) are the important
/// ones — a tool that writes when it should have refused still returns a plausible answer.
/// </summary>
public class ReceivedInvoiceWriteChatToolTests
{
    // ─── Shared fixtures ──────────────────────────────────────────────────

    private static ReceivedInvoiceDto BuildInvoice(
        long id = 7,
        string? documentNumber = "FP-2026-0042",
        EReceivedInvoiceStatus status = EReceivedInvoiceStatus.Received,
        string supplierName = "Alza.cz a.s.")
        => new()
        {
            Id = id,
            DocumentNumber = documentNumber,
            SupplierId = 42,
            SupplierName = supplierName,
            Status = status,
            IssueDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            TotalBeforeVat = 10000m,
            TotalVat = 2100m,
            TotalWithVat = 12100m,
            CurrencyCode = "CZK",
            CurrencySymbol = "Kč"
        };

    private static ApproveReceivedInvoiceTool BuildApproveTool(IReceivedInvoiceService service)
        => new(service, Substitute.For<ILogger<ApproveReceivedInvoiceTool>>());

    private static MarkReceivedInvoicePaidTool BuildMarkPaidTool(IReceivedInvoiceService service)
        => new(service, Substitute.For<ILogger<MarkReceivedInvoicePaidTool>>());

    private static DeleteReceivedInvoiceTool BuildDeleteTool(IReceivedInvoiceService service)
        => new(service, Substitute.For<ILogger<DeleteReceivedInvoiceTool>>());

    // ═══════════════════════════════════════════════════════════════════════
    //  Shared invoice lookup (exercised through approve — the same code serves
    //  mark-paid and delete, which is exactly why it is tested in one place)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approve_WithoutIdOrDocumentNumber_RefusesAndTouchesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildApproveTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("at least one of");
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_WithUnknownId_ReportsNotFoundAndTouchesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((ReceivedInvoiceDto?)null);

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["id"] = "99" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("99");
        result.ErrorMessage.ShouldContain("not found");
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_ByDocumentNumber_ActsOnTheSingleMatch()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetAllAsync(ct: Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedInvoiceDto>
            {
                BuildInvoice(id: 7, documentNumber: "FP-2026-0042"),
                BuildInvoice(id: 8, documentNumber: "FP-2026-0099")
            });
        service.ApproveAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7, status: EReceivedInvoiceStatus.Approved));

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["document_number"] = "0042" });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).ApproveAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The safety property of the whole write set: a document number is matched as a substring,
    /// so an ambiguous one must stop the tool. Acting on "the first match" would approve — or
    /// delete — an invoice the user never named.
    /// </summary>
    [Fact]
    public async Task Approve_WhenDocumentNumberMatchesSeveralInvoices_AsksWhichOneAndChangesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetAllAsync(ct: Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedInvoiceDto>
            {
                BuildInvoice(id: 7, documentNumber: "FP-2026-0042"),
                BuildInvoice(id: 8, documentNumber: "FP-2026-0043")
            });

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["document_number"] = "FP-2026-004" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("matches 2");
        result.ErrorMessage.ShouldContain("ID=7");
        result.ErrorMessage.ShouldContain("ID=8");
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_WithBothIdAndDocumentNumber_PrefersTheId()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice(id: 7));
        service.ApproveAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7, status: EReceivedInvoiceStatus.Approved));

        var result = await BuildApproveTool(service).ExecuteAsync(
            new() { ["id"] = "7", ["document_number"] = "something else entirely" });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).ApproveAsync(7, Arg.Any<CancellationToken>());
        await service.DidNotReceive().GetAllAsync(
            Arg.Any<EReceivedInvoiceStatus?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  approve_received_invoice
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Approve_OnAReceivedInvoice_ApprovesItAndReportsTheNewStatus()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.ApproveAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["id"] = "7" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Approved");
        result.OutputText.ShouldContain("FP-2026-0042");
        await service.Received(1).ApproveAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The status rule lives in ReceivedInvoiceService, which throws. The tool must turn that
    /// into a message the model can act on, not into an unhandled exception.
    /// </summary>
    [Fact]
    public async Task Approve_OnAnAlreadyPaidInvoice_ReportsTheServiceRuleAsAFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Paid));
        service.ApproveAsync(7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cannot approve invoice in Paid status. Only Received allowed."));

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["id"] = "7" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Only Received allowed");
    }

    /// <summary>
    /// Lookup and write are two round trips; the invoice can disappear in between (another user,
    /// another tab). The service answers null and the tool must not claim success.
    /// </summary>
    [Fact]
    public async Task Approve_WhenTheInvoiceVanishesBetweenLookupAndWrite_ReportsFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.ApproveAsync(7, Arg.Any<CancellationToken>()).Returns((ReceivedInvoiceDto?)null);

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["id"] = "7" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("7");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  mark_received_invoice_paid
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task MarkPaid_WithoutPaidAt_LetsTheServiceUseToday()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));
        service.MarkAsPaidAsync(7, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Paid));

        var result = await BuildMarkPaidTool(service).ExecuteAsync(new() { ["id"] = "7" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Paid");
        await service.Received(1).MarkAsPaidAsync(7, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPaid_WithPaidAt_PassesThatUtcDateOn()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));
        service.MarkAsPaidAsync(7, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Paid));

        var result = await BuildMarkPaidTool(service).ExecuteAsync(
            new() { ["id"] = "7", ["paid_at"] = "2026-04-15" });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).MarkAsPaidAsync(
            7,
            // Npgsql rejects any other DateTimeKind on a 'timestamp with time zone' column.
            Arg.Is<DateTime?>(date => date == new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc)
                                      && date.Value.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// An unreadable date must not silently become "today" — that would record the payment on
    /// the wrong day, in the wrong VAT period, with the user never told.
    /// </summary>
    [Fact]
    public async Task MarkPaid_WithAnUnreadablePaidAt_RefusesAndWritesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));

        var result = await BuildMarkPaidTool(service).ExecuteAsync(
            new() { ["id"] = "7", ["paid_at"] = "yesterday" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("paid_at");
        await service.DidNotReceive().MarkAsPaidAsync(
            Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPaid_OnAnInvoiceThatIsNotApprovedYet_ReportsTheServiceRuleAsAFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.MarkAsPaidAsync(7, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cannot mark as paid in Received status. Only Approved allowed."));

        var result = await BuildMarkPaidTool(service).ExecuteAsync(new() { ["id"] = "7" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Only Approved allowed");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  delete_received_invoice — the confirmable one
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Delete_IsTheOnlyConfirmableToolOfTheFour()
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        BuildDeleteTool(service).ShouldBeAssignableTo<IConfirmableChatTool>();

        // The other three are deliberately plain writes (see DEVGUIDE §4.7 / issue #218).
        BuildApproveTool(service).ShouldNotBeAssignableTo<IConfirmableChatTool>();
        BuildMarkPaidTool(service).ShouldNotBeAssignableTo<IConfirmableChatTool>();
    }

    [Fact]
    public async Task DeletePreview_DescribesTheInvoiceWithoutDeletingIt()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());

        var result = await BuildDeleteTool(service).BuildPreviewAsync(new() { ["id"] = "7" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FP-2026-0042");
        result.OutputText.ShouldContain("Alza.cz a.s.");
        await service.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePreview_ForAnUnknownInvoice_FailsSoNothingIsOfferedForConfirmation()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((ReceivedInvoiceDto?)null);

        var result = await BuildDeleteTool(service).BuildPreviewAsync(new() { ["id"] = "99" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    public async Task Delete_WhenExecuted_SoftDeletesTheInvoice()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.DeleteAsync(7, Arg.Any<CancellationToken>()).Returns(true);

        var result = await BuildDeleteTool(service).ExecuteAsync(
            new() { ["id"] = "7", ["confirm"] = "true" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FP-2026-0042");
        await service.Received(1).DeleteAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_OnAnApprovedInvoice_ReportsTheServiceRuleAsAFailure()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));
        service.DeleteAsync(7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Cannot delete invoice in Approved status. Only Received or Rejected allowed."));

        var result = await BuildDeleteTool(service).ExecuteAsync(
            new() { ["id"] = "7", ["confirm"] = "true" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Only Received or Rejected allowed");
    }

    [Fact]
    public async Task Delete_WhenTheServiceReportsNothingDeleted_DoesNotClaimSuccess()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.DeleteAsync(7, Arg.Any<CancellationToken>()).Returns(false);

        var result = await BuildDeleteTool(service).ExecuteAsync(
            new() { ["id"] = "7", ["confirm"] = "true" });

        result.IsSuccess.ShouldBeFalse();
    }

    // ─── The gate itself, through the real executor ───────────────────────

    /// <summary>
    /// End to end over the production <see cref="ChatToolExecutor"/>: without <c>confirm: true</c>
    /// the delete must not reach the service at all. The gate is central (#212), but only a test
    /// over the real executor proves THIS tool is wired into it.
    /// </summary>
    [Fact]
    public async Task Delete_CalledThroughTheExecutorWithoutConfirm_PreviewsAndDeletesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());

        var executor = new ChatToolExecutor(
            [BuildDeleteTool(service)],
            Substitute.For<ILogger<ChatToolExecutor>>());

        var result = await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "delete_received_invoice",
            Parameters = new Dictionary<string, string> { ["id"] = "7" }
        });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.OutputText.ShouldContain("NOTHING HAS BEEN CHANGED YET");
        await service.DidNotReceive().DeleteAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_CalledThroughTheExecutorWithConfirm_ActuallyDeletes()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.DeleteAsync(7, Arg.Any<CancellationToken>()).Returns(true);

        var executor = new ChatToolExecutor(
            [BuildDeleteTool(service)],
            Substitute.For<ILogger<ChatToolExecutor>>());

        var result = await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "delete_received_invoice",
            Parameters = new Dictionary<string, string> { ["id"] = "7", ["confirm"] = "true" }
        });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        await service.Received(1).DeleteAsync(7, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The three non-confirmable writes go straight through the same executor — pinning the
    /// decision that only the destructive one is gated (issue #218 acceptance criteria).
    /// </summary>
    [Fact]
    public async Task Approve_CalledThroughTheExecutor_RunsWithoutAskingForConfirmation()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.ApproveAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));

        var executor = new ChatToolExecutor(
            [BuildApproveTool(service)],
            Substitute.For<ILogger<ChatToolExecutor>>());

        var result = await executor.ExecuteToolAsync(new ParsedToolCall
        {
            Action = "approve_received_invoice",
            Parameters = new Dictionary<string, string> { ["id"] = "7" }
        });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        await service.Received(1).ApproveAsync(7, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  create_received_invoice
    // ═══════════════════════════════════════════════════════════════════════

    private const string OneItem = """[{"description": "Toner", "quantity": 2, "unit_price": 1500}]""";

    /// <summary>
    /// Builds the create tool with all four collaborators stubbed for the happy path:
    /// one matching supplier, a known currency and a default VAT rate.
    /// </summary>
    private static CreateReceivedInvoiceTool BuildCreateTool(
        IReceivedInvoiceService receivedInvoiceService,
        IClientService? clientService = null,
        ICurrencyService? currencyService = null,
        IVatRateService? vatRateService = null)
    {
        clientService ??= BuildClientServiceReturning(new ClientDto { Id = 42, CompanyName = "Alza.cz a.s." });

        if (currencyService is null)
        {
            currencyService = Substitute.For<ICurrencyService>();
            currencyService.GetCurrencyByCodeAsync("CZK", Arg.Any<CancellationToken>())
                .Returns(new CurrencyDto { Id = 1, Code = "CZK" });
        }

        if (vatRateService is null)
        {
            vatRateService = Substitute.For<IVatRateService>();
            vatRateService.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
                .Returns(new VatRateDto { Id = 5, Rate = 21m });
        }

        return new CreateReceivedInvoiceTool(
            receivedInvoiceService, clientService, currencyService, vatRateService,
            Substitute.For<ILogger<CreateReceivedInvoiceTool>>());
    }

    private static IClientService BuildClientServiceReturning(params ClientDto[] matches)
    {
        var clientService = Substitute.For<IClientService>();
        clientService.GetClientsPagedAsync(Arg.Any<ClientFilterDto>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<ClientDto>
            {
                Items = matches.ToList(),
                TotalCount = matches.Length,
                PageNumber = 1,
                PageSize = 5
            });
        return clientService;
    }

    [Fact]
    public async Task Create_WithSupplierAndItems_CreatesTheInvoiceAndOpensIt()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem,
            ["document_number"] = "FP-2026-0042",
            ["issue_date"] = "2026-04-01",
            ["due_date"] = "2026-04-30",
            ["variable_symbol"] = "123456"
        });

        result.IsSuccess.ShouldBeTrue();
        result.UiAction.ShouldNotBeNull();
        result.UiAction.Type.ShouldBe("navigate");
        result.UiAction.Url.ShouldBe("/received-invoices/7");

        await service.Received(1).CreateAsync(
            Arg.Is<CreateReceivedInvoiceDto>(dto =>
                dto.SupplierId == 42 &&
                dto.CurrencyId == 1 &&
                dto.DocumentNumber == "FP-2026-0042" &&
                dto.VariableSymbol == "123456" &&
                dto.IssueDate == new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) &&
                dto.DueDate == new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc) &&
                dto.Items.Count == 1 &&
                dto.Items[0].Description == "Toner" &&
                dto.Items[0].Quantity == 2m &&
                dto.Items[0].UnitPrice == 1500m &&
                dto.Items[0].VatRateId == 5 &&
                dto.Items[0].VatRatePercentage == 21m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithAnExplicitVatRateOnAnItem_KeepsItInsteadOfTheDefault()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = """[{"description": "Kniha", "unit_price": 300, "vat_rate": 12}]"""
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).CreateAsync(
            Arg.Is<CreateReceivedInvoiceDto>(dto =>
                dto.Items[0].VatRatePercentage == 12m &&
                // The id of the default 21% rate must not be carried over to a different rate.
                dto.Items[0].VatRateId == null &&
                dto.Items[0].Quantity == 1m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithUnknownSupplier_RefusesAndCreatesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var tool = BuildCreateTool(service, BuildClientServiceReturning());

        var result = await tool.ExecuteAsync(new() { ["supplier_name"] = "Nobody", ["items"] = OneItem });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Nobody");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithAnAmbiguousSupplier_AsksWhichOneAndCreatesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var tool = BuildCreateTool(service, BuildClientServiceReturning(
            new ClientDto { Id = 42, CompanyName = "Alza.cz a.s.", RegistrationNumber = "27082440" },
            new ClientDto { Id = 43, CompanyName = "Alza Media s.r.o.", RegistrationNumber = "12345678" }));

        var result = await tool.ExecuteAsync(new() { ["supplier_name"] = "Alza", ["items"] = OneItem });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Alza.cz a.s.");
        result.ErrorMessage.ShouldContain("Alza Media s.r.o.");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithAnUnknownCurrency_RefusesAndCreatesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var currencyService = Substitute.For<ICurrencyService>();
        currencyService.GetCurrencyByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((CurrencyDto?)null);

        var result = await BuildCreateTool(service, currencyService: currencyService).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem,
            ["currency"] = "XYZ"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("XYZ");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithoutCurrency_FallsBackToCzk()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));
        var currencyService = Substitute.For<ICurrencyService>();
        currencyService.GetCurrencyByCodeAsync("CZK", Arg.Any<CancellationToken>())
            .Returns(new CurrencyDto { Id = 1, Code = "CZK" });

        var result = await BuildCreateTool(service, currencyService: currencyService).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem
        });

        result.IsSuccess.ShouldBeTrue();
        await currencyService.Received(1).GetCurrencyByCodeAsync("CZK", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("[]", "at least one")]
    [InlineData("""[{"quantity": 1, "unit_price": 100}]""", "description")]
    [InlineData("""[{"description": "Toner"}]""", "unit_price")]
    public async Task Create_WithUnusableItems_RefusesAndCreatesNothing(string itemsJson, string expectedHint)
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = itemsJson
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain(expectedHint);
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A date the tool cannot read must stop the create. Dropping it would silently file the
    /// expense into the wrong VAT period — the one mistake an accountant cannot spot later.
    /// </summary>
    [Fact]
    public async Task Create_WithAnUnreadableDate_RefusesAndCreatesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem,
            ["taxable_supply_date"] = "last friday"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("taxable_supply_date");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WhenTheServiceRejectsTheInvoice_ReportsTheReason()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Supplier is not active."));

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Supplier is not active.");
    }
}
