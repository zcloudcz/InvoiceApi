using System.Globalization;
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
        result.ErrorMessage!.ShouldContain("at least one of");
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_WithUnknownId_ReportsNotFoundAndTouchesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((ReceivedInvoiceDto?)null);

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["id"] = "99" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("99");
        result.ErrorMessage!.ShouldContain("not found");
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
        result.ErrorMessage!.ShouldContain("matches 2");
        result.ErrorMessage!.ShouldContain("ID=7");
        result.ErrorMessage!.ShouldContain("ID=8");
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

    [Fact]
    public async Task Approve_WithANonNumericId_RefusesInsteadOfThrowing()
    {
        // The executor type-checks 'id' before a tool runs, so this guard only covers direct
        // callers - but "the model sent the document number as the id" has to come back as a
        // message it can act on, never as an unhandled FormatException.
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["id"] = "FP-2026-0042" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("numeric");
        await service.DidNotReceive().GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_WithADocumentNumberThatMatchesNothing_SaysSoAndChangesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetAllAsync(ct: Arg.Any<CancellationToken>())
            .Returns(new List<ReceivedInvoiceDto> { BuildInvoice(id: 7, documentNumber: "FP-2026-0042") });

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["document_number"] = "FP-2025-0001" });

        result.IsSuccess.ShouldBeFalse();
        // The number the user said, so they can see the typo - not just "not found".
        result.ErrorMessage!.ShouldContain("FP-2025-0001");
        result.ErrorMessage!.ShouldContain("not found");
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The candidate list is capped so a model does not get fifty rows to choose from. The cap is
    /// only safe while the number of hidden matches is still reported - otherwise the user picks
    /// from ten and believes those ten are all there is.
    /// </summary>
    [Fact]
    public async Task Approve_WhenDocumentNumberMatchesMoreThanTheListCap_ShowsTenAndCountsTheRest()
    {
        const int matchCount = 12;
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetAllAsync(ct: Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(1, matchCount)
                .Select(number => BuildInvoice(id: number, documentNumber: $"FP-2026-{number:0000}"))
                .ToList());

        var result = await BuildApproveTool(service).ExecuteAsync(new() { ["document_number"] = "FP-2026-" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain($"matches {matchCount}");
        result.ErrorMessage!.ShouldContain("and 2 more");
        result.ErrorMessage!.ShouldContain("ID=10");     // the tenth row is still listed
        result.ErrorMessage!.ShouldNotContain("ID=11");  // the eleventh is only in the "2 more"
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
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
        result.ErrorMessage!.ShouldContain("Only Received allowed");
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
        result.ErrorMessage!.ShouldContain("7");
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
        result.ErrorMessage!.ShouldContain("paid_at");
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
        result.ErrorMessage!.ShouldContain("Only Approved allowed");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  The confirm gate — all four writes are behind it
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// DEVGUIDE §4.7 rule 7: every NEW write tool is <see cref="IConfirmableChatTool"/>.
    /// Pinned as one test over all seven received-invoice tools, because the value is the
    /// contrast — writes ask, reads do not. A future tool that quietly drops the gate
    /// (or a read tool that grows one) fails here.
    /// </summary>
    [Fact]
    public void AllFourWriteTools_AreConfirmable_AndTheReadOnesAreNot()
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        BuildCreateTool(service).ShouldBeAssignableTo<IConfirmableChatTool>();
        BuildApproveTool(service).ShouldBeAssignableTo<IConfirmableChatTool>();
        BuildMarkPaidTool(service).ShouldBeAssignableTo<IConfirmableChatTool>();
        BuildDeleteTool(service).ShouldBeAssignableTo<IConfirmableChatTool>();

        new GetReceivedInvoiceTool(service, Substitute.For<ILogger<GetReceivedInvoiceTool>>())
            .ShouldNotBeAssignableTo<IConfirmableChatTool>();
        new ListReceivedInvoicesTool(service, Substitute.For<IClientService>(), Substitute.For<ILogger<ListReceivedInvoicesTool>>())
            .ShouldNotBeAssignableTo<IConfirmableChatTool>();
        new SearchReceivedInvoicesTool(service, Substitute.For<ILogger<SearchReceivedInvoicesTool>>())
            .ShouldNotBeAssignableTo<IConfirmableChatTool>();
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
    public async Task DeletePreview_ForAnInvoiceWithoutADocumentNumber_StillNamesTheDocument()
    {
        // Suppliers do send unnumbered documents. The preview is the last thing the user reads
        // before an irreversible write, so a missing number has to read as "(no number)" and the
        // rest of the identification must still be there.
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(documentNumber: null));

        var result = await BuildDeleteTool(service).BuildPreviewAsync(new() { ["id"] = "7" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("(no number)");
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
        result.ErrorMessage!.ShouldContain("not found");
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
        result.ErrorMessage!.ShouldContain("Only Received or Rejected allowed");
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

    // ─── approve / mark-paid / create through the same executor ───────────

    /// <summary>
    /// Approving is irreversible — there is no un-approve, and an approved invoice can no longer
    /// be deleted either. So the first call must only describe the change.
    /// </summary>
    [Fact]
    public async Task Approve_CalledThroughTheExecutorWithoutConfirm_PreviewsAndApprovesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());

        var result = await ExecuteThroughExecutorAsync(
            BuildApproveTool(service), "approve_received_invoice", new() { ["id"] = "7" });

        result.RequiresConfirmation.ShouldBeTrue();
        result.OutputText.ShouldContain("FP-2026-0042");
        result.OutputText.ShouldContain("NOTHING HAS BEEN CHANGED YET");
        await service.DidNotReceive().ApproveAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_CalledThroughTheExecutorWithConfirm_ActuallyApproves()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(BuildInvoice());
        service.ApproveAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));

        var result = await ExecuteThroughExecutorAsync(
            BuildApproveTool(service), "approve_received_invoice",
            new() { ["id"] = "7", ["confirm"] = "true" });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        await service.Received(1).ApproveAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPaid_CalledThroughTheExecutorWithoutConfirm_PreviewsAndPaysNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));

        var result = await ExecuteThroughExecutorAsync(
            BuildMarkPaidTool(service), "mark_received_invoice_paid",
            new() { ["id"] = "7", ["paid_at"] = "2026-04-15" });

        result.RequiresConfirmation.ShouldBeTrue();
        // The preview has to name the date, otherwise the user approves a day they never saw.
        result.OutputText.ShouldContain("2026-04-15");
        await service.DidNotReceive().MarkAsPaidAsync(
            Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkPaid_CalledThroughTheExecutorWithConfirm_ActuallyPays()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Approved));
        service.MarkAsPaidAsync(7, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EReceivedInvoiceStatus.Paid));

        var result = await ExecuteThroughExecutorAsync(
            BuildMarkPaidTool(service), "mark_received_invoice_paid",
            new() { ["id"] = "7", ["confirm"] = "true" });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        await service.Received(1).MarkAsPaidAsync(7, Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_CalledThroughTheExecutorWithoutConfirm_PreviewsAndCreatesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await ExecuteThroughExecutorAsync(
            BuildCreateTool(service), "create_received_invoice",
            new() { ["supplier_name"] = "Alza", ["items"] = OneItem });

        result.RequiresConfirmation.ShouldBeTrue();
        result.OutputText.ShouldContain("Alza.cz a.s.");
        // Item count and amount, so the user approves a number and not just a supplier name.
        result.OutputText.ShouldContain("1 item");
        // 2 × 1500 excluding VAT. Formatted through the same "N2" the tool uses, so the
        // assertion does not depend on the culture the test host happens to run under.
        result.OutputText.ShouldContain(3000m.ToString("N2"));
        // A preview must not move the browser — the record does not exist yet.
        result.UiAction.ShouldBeNull();
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_CalledThroughTheExecutorWithConfirm_ActuallyCreates()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));

        var result = await ExecuteThroughExecutorAsync(
            BuildCreateTool(service), "create_received_invoice",
            new() { ["supplier_name"] = "Alza", ["items"] = OneItem, ["confirm"] = "true" });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        result.UiAction!.Url.ShouldBe("/received-invoices/7");
        await service.Received(1).CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Runs one tool through the production <see cref="ChatToolExecutor"/> — the only way to prove
    /// the gate actually applies to THIS tool, since the gate itself lives in the executor (#212).
    /// </summary>
    private static Task<ChatToolResult> ExecuteThroughExecutorAsync(
        IChatTool tool,
        string action,
        Dictionary<string, string> parameters)
    {
        var executor = new ChatToolExecutor([tool], Substitute.For<ILogger<ChatToolExecutor>>());

        return executor.ExecuteToolAsync(new ParsedToolCall { Action = action, Parameters = parameters });
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

        vatRateService ??= BuildVatRateService();

        return new CreateReceivedInvoiceTool(
            receivedInvoiceService, clientService, currencyService, vatRateService,
            Substitute.For<ILogger<CreateReceivedInvoiceTool>>());
    }

    /// <summary>
    /// The VAT rate collaborator of the happy path: a default 21 % rate, plus the seeded rates a
    /// dictated <c>vat_rate</c> is checked against (see <see cref="SeededVatRates"/>). Built
    /// apart from <see cref="BuildCreateTool"/> so a test can keep the substitute and assert
    /// WHICH date the rates were asked for.
    /// </summary>
    private static IVatRateService BuildVatRateService()
    {
        var vatRateService = Substitute.For<IVatRateService>();
        vatRateService.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
            .Returns(new VatRateDto { Id = 5, Rate = 21m });
        vatRateService.GetActiveVatRatesForDateAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(SeededVatRates());
        return vatRateService;
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

    /// <summary>
    /// The three rates <c>TenantDbContext</c> seeds: standard 21 %, reduced 12 % and the 0 %
    /// "osvobozeno od daně" row. 0 % is a real, active rate — so it must stay accepted.
    /// </summary>
    private static List<VatRateDto> SeededVatRates() =>
    [
        new() { Id = 5, Rate = 21m },
        new() { Id = 6, Rate = 12m },
        new() { Id = 3, Rate = 0m }
    ];

    [Theory]
    [InlineData(-21, "-21")]   // lower bound: a minus sign turns the VAT return upside down
    [InlineData(500, "500")]   // upper bound: nothing below this tool would have caught it
    public async Task Create_WithAVatRateThatIsNotSetUp_RefusesAndCreatesNothing(
        int dictatedRate, string expectedInMessage)
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = $"[{{\"description\": \"Toner\", \"unit_price\": 1500, \"vat_rate\": {dictatedRate}}}]"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain(expectedInMessage);
        // The answer has to name the way out, otherwise the model just retries the same number.
        result.ErrorMessage!.ShouldContain("21%");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithTheZeroPercentRate_IsAcceptedBecauseItIsARealRate()
    {
        // Guards the check from being written as "the rate must be positive": 0 % (osvobozeno
        // od daně) is a seeded, active rate and invoices from non-VAT-payers carry it.
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = """[{"description": "Poradenství", "unit_price": 1000, "vat_rate": 0}]"""
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).CreateAsync(
            Arg.Is<CreateReceivedInvoiceDto>(dto => dto.Items[0].VatRatePercentage == 0m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_PreviewWithAVatRateThatIsNotSetUp_RefusesToo()
    {
        // The gate is only useful if the preview refuses as well — otherwise the user would
        // approve an expense that the confirmed call then rejects.
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildCreateTool(service).BuildPreviewAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = """[{"description": "Toner", "unit_price": 1500, "vat_rate": 99}]"""
        });

        result.IsSuccess.ShouldBeFalse();
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithADictatedRateAndNoRatesSetUp_SaysSoInsteadOfListingNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var vatRateService = Substitute.For<IVatRateService>();
        vatRateService.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
            .Returns((VatRateDto?)null);
        vatRateService.GetActiveVatRatesForDateAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var result = await BuildCreateTool(service, vatRateService: vatRateService).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = """[{"description": "Toner", "unit_price": 1500, "vat_rate": 21}]"""
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("No VAT rates are set up");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Issue #283, Nález 1: a missing default rate used to fall through to a silent 0% instead of
    /// being refused. An item that omits 'vat_rate' has nothing else to fall back on, so a
    /// missing default must stop the create, not save a tax document with no VAT at all.
    /// </summary>
    [Fact]
    public async Task Create_WithNoDefaultVatRateAndNoDictatedRate_RefusesAndCreatesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var vatRateService = Substitute.For<IVatRateService>();
        vatRateService.GetDefaultStandardRateAsync(Arg.Any<CancellationToken>())
            .Returns((VatRateDto?)null);
        vatRateService.GetActiveVatRatesForDateAsync(Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(SeededVatRates());

        var result = await BuildCreateTool(service, vatRateService: vatRateService).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("vat_rate");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Issue #283, Nález 2: an explicit non-positive quantity used to silently become 1.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task Create_WithAnExplicitNonPositiveQuantity_RefusesAndCreatesNothing(decimal quantity)
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = $"[{{\"description\": \"Toner\", \"quantity\": {quantity.ToString(CultureInfo.InvariantCulture)}, \"unit_price\": 1500}}]"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("quantity");
        await service.DidNotReceive().CreateAsync(
            Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_ChecksTheRatesValidOnTheDayOfSupply_NotToday()
    {
        // An expense recorded months late must be checked against the rates that applied then,
        // not against today's — otherwise an old but legal rate would be refused.
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));

        var vatRateService = BuildVatRateService();

        await BuildCreateTool(service, vatRateService: vatRateService).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem,
            ["taxable_supply_date"] = "2023-06-30"
        });

        await vatRateService.Received(1).GetActiveVatRatesForDateAsync(
            new DateTime(2023, 6, 30, 0, 0, 0, DateTimeKind.Utc), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pins WHICH date the rate check is anchored to: <c>taxable_supply_date ?? issue_date</c>.
    /// The test above sends only the supply date, so the two dates are interchangeable there and
    /// a swapped fallback would still pass. These three rows are the cases that tell them apart.
    /// </summary>
    [Theory]
    // Both dictated - the supply date decides. Reversing the fallback would ask about 2026-04-01
    // and could refuse a rate that was legal on the day the expense actually happened.
    [InlineData("2026-04-01", "2023-06-30", "2023-06-30")]
    // Only the issue date - it stands in. Dropping the fallback would ask about today instead,
    // which is exactly wrong for an expense recorded months late.
    [InlineData("2023-06-30", null, "2023-06-30")]
    // Neither - the tool has nothing to anchor on and must not invent a date; VatRateService
    // substitutes today itself.
    [InlineData(null, null, null)]
    public async Task Create_ChecksTheRatesValidOnTheDateTheDocumentBelongsTo(
        string? issueDate, string? taxableSupplyDate, string? expectedRateDate)
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));
        var vatRateService = BuildVatRateService();

        var parameters = new Dictionary<string, string>
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem
        };
        if (issueDate is not null)
            parameters["issue_date"] = issueDate;
        if (taxableSupplyDate is not null)
            parameters["taxable_supply_date"] = taxableSupplyDate;

        var result = await BuildCreateTool(service, vatRateService: vatRateService).ExecuteAsync(parameters);

        result.IsSuccess.ShouldBeTrue();
        await vatRateService.Received(1).GetActiveVatRatesForDateAsync(
            ParseUtcDate(expectedRateDate), Arg.Any<CancellationToken>());
    }

    /// <summary>An ISO date the way the chat tools accept it, or null for "no date at all".</summary>
    private static DateTime? ParseUtcDate(string? isoDate)
        => isoDate is null
            ? null
            : DateTime.SpecifyKind(
                DateTime.ParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateTimeKind.Utc);

    [Fact]
    public async Task Create_WithALowercaseCurrencyCode_LooksItUpInUpperCase()
    {
        // A model passes through whatever the user said ("eur"), while currency codes are stored
        // upper-case - without the normalisation a perfectly valid expense would be refused.
        var service = Substitute.For<IReceivedInvoiceService>();
        service.CreateAsync(Arg.Any<CreateReceivedInvoiceDto>(), Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(id: 7));
        var currencyService = Substitute.For<ICurrencyService>();
        currencyService.GetCurrencyByCodeAsync("EUR", Arg.Any<CancellationToken>())
            .Returns(new CurrencyDto { Id = 2, Code = "EUR" });

        var result = await BuildCreateTool(service, currencyService: currencyService).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = OneItem,
            ["currency"] = "eur"
        });

        result.IsSuccess.ShouldBeTrue();
        await service.Received(1).CreateAsync(
            Arg.Is<CreateReceivedInvoiceDto>(dto => dto.CurrencyId == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithUnknownSupplier_RefusesAndCreatesNothing()
    {
        var service = Substitute.For<IReceivedInvoiceService>();
        var tool = BuildCreateTool(service, BuildClientServiceReturning());

        var result = await tool.ExecuteAsync(new() { ["supplier_name"] = "Nobody", ["items"] = OneItem });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("Nobody");
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
        result.ErrorMessage!.ShouldContain("Alza.cz a.s.");
        result.ErrorMessage!.ShouldContain("Alza Media s.r.o.");
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
        result.ErrorMessage!.ShouldContain("XYZ");
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
    // A JSON array of nulls passes the executor's "is it an array?" check, so the tool has to
    // answer it itself — like CreateInvoiceTool does — instead of dereferencing a null item.
    [InlineData("[null]", "Item #1")]
    public async Task Create_WithUnusableItems_RefusesAndCreatesNothing(string itemsJson, string expectedHint)
    {
        var service = Substitute.For<IReceivedInvoiceService>();

        var result = await BuildCreateTool(service).ExecuteAsync(new()
        {
            ["supplier_name"] = "Alza",
            ["items"] = itemsJson
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain(expectedHint);
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
        result.ErrorMessage!.ShouldContain("taxable_supply_date");
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
        result.ErrorMessage!.ShouldContain("Supplier is not active.");
    }
}
