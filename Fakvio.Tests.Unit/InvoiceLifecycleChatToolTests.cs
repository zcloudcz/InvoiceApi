using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the issued-invoice lifecycle chat tools (issue #217):
///   - GetInvoiceTool          (read — covers MCP GetInvoice + FindInvoiceByNumber)
///   - CompleteInvoiceTool     (write, confirmable)
///   - MarkInvoicePaidTool     (write, confirmable)
///   - SendInvoiceEmailTool    (write, confirmable)
///   - DeleteInvoiceTool       (destructive, confirmable)
///
/// Everything is mocked with NSubstitute — no database, no SMTP server. What the tools own is
/// parameter parsing, the status preconditions, the error paths, and the difference between a
/// preview (nothing written) and an execution.
///
/// These are the first production tools behind the confirm gate from #212, so the last section
/// runs them through a REAL <see cref="ChatToolExecutor"/>: a tool that is confirmable in
/// theory but reachable without approval in practice would still delete drafts silently.
///
/// Junior note: assertions avoid comparing formatted amounts. <c>:N2</c> uses the current
/// culture, so "12 100,00" and "12,100.00" are both correct depending on the machine.
/// </summary>
public class InvoiceLifecycleChatToolTests
{
    private readonly IInvoiceService _invoiceService = Substitute.For<IInvoiceService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    // ─── Shared helpers ───────────────────────────────────────────────────

    private static InvoiceDto BuildInvoice(
        long id = 42,
        string? documentNumber = "FAK-2026-001",
        EInvoiceStatus status = EInvoiceStatus.Draft,
        EDocumentType documentType = EDocumentType.Invoice,
        bool isSentByEmail = false)
        => new()
        {
            Id = id,
            DocumentNumber = documentNumber,
            DocumentType = documentType,
            Status = status,
            ClientName = "Alza.cz",
            IssuerName = "Moje firma s.r.o.",
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DueDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            VariableSymbol = "2026001",
            TotalBeforeVat = 10000m,
            TotalVat = 2100m,
            TotalWithVat = 12100m,
            CurrencyCode = "CZK",
            IsSentByEmail = isSentByEmail,
            LastSentByEmailAt = isSentByEmail ? new DateTime(2026, 3, 2, 8, 0, 0, DateTimeKind.Utc) : null,
            InvoiceItem =
            [
                new InvoiceItemDto
                {
                    OrderIndex = 0,
                    Description = "Web development",
                    Quantity = 10m,
                    Unit = "hod",
                    UnitPrice = 1000m,
                    VatRatePercentage = 21m,
                    TotalBeforeVat = 10000m,
                    VatAmount = 2100m,
                    TotalWithVat = 12100m
                }
            ]
        };

    /// <summary>Registers the invoice so both lookup paths (id and number) find it.</summary>
    private void GivenInvoice(InvoiceDto invoice)
    {
        _invoiceService.GetInvoiceByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);

        if (invoice.DocumentNumber is not null)
        {
            _invoiceService
                .GetInvoiceByDocumentNumberAsync(invoice.DocumentNumber, Arg.Any<CancellationToken>())
                .Returns(invoice);
        }
    }

    private static Dictionary<string, string> ById(long id = 42) => new() { ["id"] = id.ToString() };

    private GetInvoiceTool GetTool() => new(_invoiceService);

    private CompleteInvoiceTool CompleteTool()
        => new(_invoiceService, Substitute.For<ILogger<CompleteInvoiceTool>>());

    private MarkInvoicePaidTool MarkPaidTool()
        => new(_invoiceService, Substitute.For<ILogger<MarkInvoicePaidTool>>());

    private SendInvoiceEmailTool SendEmailTool()
        => new(_invoiceService, _emailService, Substitute.For<ILogger<SendInvoiceEmailTool>>());

    private DeleteInvoiceTool DeleteTool()
        => new(_invoiceService, Substitute.For<ILogger<DeleteInvoiceTool>>());

    // ─── get_invoice ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetInvoice_ById_ReturnsTheWholeDocument()
    {
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));

        var result = await GetTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FAK-2026-001");
        result.OutputText.ShouldContain("Completed");
        result.OutputText.ShouldContain("Alza.cz");
        result.OutputText.ShouldContain("2026-03-15");          // due date
        result.OutputText.ShouldContain("Web development");     // line items are included
        result.OutputText.ShouldContain("Line items (1)");
    }

    [Fact]
    public async Task GetInvoice_ByDocumentNumber_FindsTheSameDocument()
    {
        GivenInvoice(BuildInvoice());

        var result = await GetTool().ExecuteAsync(new Dictionary<string, string>
        {
            // Padded on purpose: the executor dispatches the raw model value.
            ["document_number"] = "  FAK-2026-001  "
        });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FAK-2026-001");
    }

    [Fact]
    public async Task GetInvoice_WithBothKeys_PrefersTheId()
    {
        GivenInvoice(BuildInvoice());

        await GetTool().ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "42",
            ["document_number"] = "FAK-2026-001"
        });

        // The id is exact; a document number is only unique per number sequence.
        await _invoiceService.Received(1).GetInvoiceByIdAsync(42, Arg.Any<CancellationToken>());
        await _invoiceService.DidNotReceive()
            .GetInvoiceByDocumentNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInvoice_WithNoKeyAtAll_ExplainsWhatToSend()
    {
        // "at least one of" is a rule the JSON schema cannot express, so the tool enforces it.
        var result = await GetTool().ExecuteAsync([]);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("id");
        result.ErrorMessage.ShouldContain("document_number");
        await _invoiceService.DidNotReceive().GetInvoiceByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetInvoice_UnknownId_ReportsTheIdBack()
    {
        _invoiceService.GetInvoiceByIdAsync(99, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        var result = await GetTool().ExecuteAsync(ById(99));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("99");
    }

    [Fact]
    public async Task GetInvoice_UnknownDocumentNumber_ReportsTheNumberBack()
    {
        _invoiceService
            .GetInvoiceByDocumentNumberAsync("FAK-9999", Arg.Any<CancellationToken>())
            .Returns((InvoiceDto?)null);

        var result = await GetTool().ExecuteAsync(
            new Dictionary<string, string> { ["document_number"] = "FAK-9999" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("FAK-9999");
    }

    [Fact]
    public async Task GetInvoice_NonNumericId_FailsInsteadOfFallingBackToTheNumber()
    {
        // The central validation rejects a non-integer id before the tool is reached, so this
        // only happens on a direct call — but falling through to the document_number lookup
        // would answer a different question than the one asked.
        var result = await GetTool().ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "abc",
            ["document_number"] = "FAK-2026-001"
        });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("abc");
        await _invoiceService.DidNotReceive()
            .GetInvoiceByDocumentNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── complete_invoice ─────────────────────────────────────────────────

    [Fact]
    public async Task CompleteInvoice_Preview_DescribesTheChange_AndWritesNothing()
    {
        GivenInvoice(BuildInvoice());

        var result = await CompleteTool().BuildPreviewAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FAK-2026-001");
        result.OutputText.ShouldContain("Draft → Completed");
        await _invoiceService.DidNotReceive()
            .CompleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteInvoice_Preview_FailsForANonDraft()
    {
        // The preview is where the user learns the operation is impossible — before approving.
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Paid));

        var result = await CompleteTool().BuildPreviewAsync(ById());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Paid");
        result.ErrorMessage.ShouldContain("Draft");
    }

    [Fact]
    public async Task CompleteInvoice_Execute_IssuesTheDraft_AndReportsTheNewNumber()
    {
        GivenInvoice(BuildInvoice(documentNumber: null));
        _invoiceService.CompleteInvoiceAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EInvoiceStatus.Completed));

        var result = await CompleteTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FAK-2026-001");
        result.OutputText.ShouldContain("Completed");
        await _invoiceService.Received(1).CompleteInvoiceAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteInvoice_Execute_RechecksTheStatus_BecausePreviewAndWriteAreNotPaired()
    {
        // The user may have issued the draft elsewhere between the preview and the approval.
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));

        var result = await CompleteTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeFalse();
        await _invoiceService.DidNotReceive()
            .CompleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteInvoice_Execute_FailsForAnUnknownInvoice()
    {
        _invoiceService.GetInvoiceByIdAsync(99, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        var result = await CompleteTool().ExecuteAsync(ById(99));

        result.IsSuccess.ShouldBeFalse();
        await _invoiceService.DidNotReceive()
            .CompleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ─── mark_invoice_paid ────────────────────────────────────────────────

    [Fact]
    public async Task MarkInvoicePaid_Preview_DescribesTheChange_AndWritesNothing()
    {
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));

        var result = await MarkPaidTool().BuildPreviewAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FAK-2026-001");
        result.OutputText.ShouldContain("Completed → Paid");
        await _invoiceService.DidNotReceive()
            .MarkAsPaidAsync(Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EInvoiceStatus.Draft)]
    [InlineData(EInvoiceStatus.Paid)]
    public async Task MarkInvoicePaid_Preview_FailsUnlessTheInvoiceIsCompleted(EInvoiceStatus status)
    {
        GivenInvoice(BuildInvoice(status: status));

        var result = await MarkPaidTool().BuildPreviewAsync(ById());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain(status.ToString());
    }

    [Fact]
    public async Task MarkInvoicePaid_Execute_MarksTheInvoice_WithTheServiceDefaultDate()
    {
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));
        var paid = BuildInvoice(status: EInvoiceStatus.Paid);
        paid.PaidAt = new DateTime(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
        _invoiceService.MarkAsPaidAsync(42, null, Arg.Any<CancellationToken>()).Returns(paid);

        var result = await MarkPaidTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("2026-03-10");
        // null = "now", decided by the service — the tool does not invent a payment date.
        await _invoiceService.Received(1).MarkAsPaidAsync(42, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MarkInvoicePaid_Execute_RechecksTheStatus()
    {
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Draft));

        var result = await MarkPaidTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeFalse();
        await _invoiceService.DidNotReceive()
            .MarkAsPaidAsync(Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
    }

    // ─── send_invoice_email ───────────────────────────────────────────────

    private static Dictionary<string, string> SendParameters(string recipient = "ucetni@alza.cz")
        => new() { ["id"] = "42", ["recipient_email"] = recipient };

    [Fact]
    public async Task SendInvoiceEmail_Preview_NamesTheRecipient_AndSendsNothing()
    {
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));

        var result = await SendEmailTool().BuildPreviewAsync(SendParameters());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("ucetni@alza.cz");
        result.OutputText.ShouldContain("FAK-2026-001");
        await _emailService.DidNotReceive()
            .SendInvoiceEmailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendInvoiceEmail_Preview_WarnsThatTheInvoiceWasAlreadySent()
    {
        // Sending twice is legitimate, but the user should know they are doing it.
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed, isSentByEmail: true));

        var result = await SendEmailTool().BuildPreviewAsync(SendParameters());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("already sent");
        result.OutputText.ShouldContain("2026-03-02");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("ucetni@")]
    [InlineData("   ")]
    public async Task SendInvoiceEmail_RejectsAnUnusableAddress_BeforeTouchingTheInvoice(string recipient)
    {
        var result = await SendEmailTool().BuildPreviewAsync(SendParameters(recipient));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("recipient_email");
        await _invoiceService.DidNotReceive().GetInvoiceByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendInvoiceEmail_Execute_SendsToTheTrimmedAddress()
    {
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));

        var result = await SendEmailTool().ExecuteAsync(SendParameters("  ucetni@alza.cz  "));

        result.IsSuccess.ShouldBeTrue();
        await _emailService.Received(1)
            .SendInvoiceEmailAsync(42, "ucetni@alza.cz", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendInvoiceEmail_Execute_SendsNothing_WhenTheInvoiceIsGone()
    {
        _invoiceService.GetInvoiceByIdAsync(42, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        var result = await SendEmailTool().ExecuteAsync(SendParameters());

        result.IsSuccess.ShouldBeFalse();
        await _emailService.DidNotReceive()
            .SendInvoiceEmailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── delete_invoice ───────────────────────────────────────────────────

    [Fact]
    public async Task DeleteInvoice_Preview_SaysItIsRecoverable_AndDeletesNothing()
    {
        GivenInvoice(BuildInvoice());

        var result = await DeleteTool().BuildPreviewAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FAK-2026-001");
        result.OutputText.ShouldContain("restored");
        await _invoiceService.DidNotReceive()
            .DeleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(EInvoiceStatus.Completed)]
    [InlineData(EInvoiceStatus.Paid)]
    [InlineData(EInvoiceStatus.Deleted)]
    public async Task DeleteInvoice_RefusesAnythingButADraft(EInvoiceStatus status)
    {
        // Stricter than the service on purpose: chat never removes a numbered tax document,
        // because the confirm gate is a UX flow, not an authorization boundary.
        GivenInvoice(BuildInvoice(status: status));

        var preview = await DeleteTool().BuildPreviewAsync(ById());
        var execution = await DeleteTool().ExecuteAsync(ById());

        preview.IsSuccess.ShouldBeFalse();
        execution.IsSuccess.ShouldBeFalse();
        execution.ErrorMessage.ShouldContain("Draft");
        await _invoiceService.DidNotReceive()
            .DeleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteInvoice_Execute_DeletesTheDraft()
    {
        GivenInvoice(BuildInvoice());
        _invoiceService.DeleteInvoiceAsync(42, Arg.Any<CancellationToken>()).Returns(true);

        var result = await DeleteTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("FAK-2026-001");
        await _invoiceService.Received(1).DeleteInvoiceAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteInvoice_Execute_ReportsARefusalFromTheService()
    {
        // The service has rules of its own (linked documents, numbering) and answers false.
        GivenInvoice(BuildInvoice());
        _invoiceService.DeleteInvoiceAsync(42, Arg.Any<CancellationToken>()).Returns(false);

        var result = await DeleteTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("42");
    }


    // ─── How get_invoice renders the parts a reader asks about ────

    [Fact]
    public async Task GetInvoice_WithoutLineItems_SaysThereAreNone()
    {
        // A draft can legitimately have no lines yet. Saying so explicitly is what stops the
        // model from claiming the listing was merely truncated.
        var withoutItems = BuildInvoice();
        withoutItems.InvoiceItem = [];
        GivenInvoice(withoutItems);

        var result = await GetTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Line items: (none)");
    }

    [Fact]
    public async Task GetInvoice_CreditNote_NamesTheDocumentItCorrects()
    {
        // "A credit note against what?" is the first question anyone asks about a dobropis.
        var creditNote = BuildInvoice(documentType: EDocumentType.CreditNote, status: EInvoiceStatus.Completed);
        creditNote.OriginalInvoiceId = 7;
        creditNote.OriginalInvoiceNumber = "FAK-2025-119";
        GivenInvoice(creditNote);

        var result = await GetTool().ExecuteAsync(ById());

        result.OutputText.ShouldContain("Related document");
        result.OutputText.ShouldContain("FAK-2025-119");
    }

    // ─── Shared identity resolution (InvoiceLookup) ───────────────────

    [Fact]
    public async Task Lookup_PaddedId_IsStillResolvedById()
    {
        // The executor validates the type but dispatches the raw model value (#268).
        GivenInvoice(BuildInvoice());

        var result = await GetTool().ExecuteAsync(new Dictionary<string, string> { ["id"] = "  42  " });

        result.IsSuccess.ShouldBeTrue();
        await _invoiceService.Received(1).GetInvoiceByIdAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Lookup_BlankId_FallsBackToTheDocumentNumber()
    {
        // Models like to fill every field, empty string included. That must not turn a call
        // carrying a perfectly good document number into "invalid id".
        GivenInvoice(BuildInvoice());

        var result = await GetTool().ExecuteAsync(new Dictionary<string, string>
        {
            ["id"] = "   ",
            ["document_number"] = "FAK-2026-001"
        });

        result.IsSuccess.ShouldBeTrue();
        await _invoiceService.Received(1)
            .GetInvoiceByDocumentNumberAsync("FAK-2026-001", Arg.Any<CancellationToken>());
    }

    // ─── The write itself came back empty (the record vanished meanwhile) ─

    [Fact]
    public async Task CompleteInvoice_Execute_Fails_WhenTheServiceReturnsNothing()
    {
        // The re-check found a draft, but the row was gone by the time of the write. The tool
        // must report that instead of dereferencing the missing result.
        GivenInvoice(BuildInvoice());
        _invoiceService.CompleteInvoiceAsync(42, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        var result = await CompleteTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("42");
    }

    [Fact]
    public async Task MarkInvoicePaid_Execute_Fails_WhenTheServiceReturnsNothing()
    {
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));
        _invoiceService.MarkAsPaidAsync(42, null, Arg.Any<CancellationToken>()).Returns((InvoiceDto?)null);

        var result = await MarkPaidTool().ExecuteAsync(ById());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("42");
    }

    [Fact]
    public async Task SendInvoiceEmail_Execute_AlsoSendsADraft_LikeTheEmailActionInTheGrid()
    {
        // Deliberate asymmetry against the other three writes: the grid offers Email for any
        // status (USERGUIDE §2.3). A status guard added here would quietly take that away.
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Draft));

        var result = await SendEmailTool().ExecuteAsync(SendParameters());

        result.IsSuccess.ShouldBeTrue();
        await _emailService.Received(1)
            .SendInvoiceEmailAsync(42, "ucetni@alza.cz", Arg.Any<CancellationToken>());
    }

    // ─── The confirm gate over the real tools (issue #212 seam) ───────────

    /// <summary>
    /// A real executor over one real tool — no mirrors, no stubs. This is what runs in
    /// production for a model answer that carries (or omits) the approval flag.
    /// </summary>
    private ChatToolExecutor ExecutorOver(IChatTool tool)
        => new([tool], Substitute.For<ILogger<ChatToolExecutor>>());

    [Fact]
    public async Task DeleteInvoice_ThroughTheExecutor_WithoutConfirm_OnlyPreviews()
    {
        GivenInvoice(BuildInvoice());
        var executor = ExecutorOver(DeleteTool());

        var result = await executor.ExecuteToolAsync(
            new ParsedToolCall { Action = "delete_invoice", Parameters = ById() });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.OutputText.ShouldContain("NOTHING HAS BEEN CHANGED YET");
        await _invoiceService.DidNotReceive()
            .DeleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteInvoice_ThroughTheExecutor_WithConfirm_Deletes()
    {
        GivenInvoice(BuildInvoice());
        _invoiceService.DeleteInvoiceAsync(42, Arg.Any<CancellationToken>()).Returns(true);
        var executor = ExecutorOver(DeleteTool());

        var parameters = ById();
        parameters["confirm"] = "true";

        var result = await executor.ExecuteToolAsync(
            new ParsedToolCall { Action = "delete_invoice", Parameters = parameters });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        await _invoiceService.Received(1).DeleteInvoiceAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteTool_ThroughTheExecutor_WithAFailingPreview_IsReportedAsNotExecuted()
    {
        // #217 follow-up from the #258 review: a preview that could not be prepared changed
        // nothing either, so the result must not look like an executed tool that failed.
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Paid));   // a Paid invoice cannot be issued
        var executor = ExecutorOver(CompleteTool());

        var result = await executor.ExecuteToolAsync(
            new ParsedToolCall { Action = "complete_invoice", Parameters = ById() });

        result.IsSuccess.ShouldBeFalse();
        result.RequiresConfirmation.ShouldBeTrue();
        result.OutputText.ShouldNotContain("NOTHING HAS BEEN CHANGED YET");
    }

    [Fact]
    public void EveryWriteToolInTheLifecycle_IsConfirmable()
    {
        // A data-changing tool that forgot IConfirmableChatTool would write on the first call,
        // and only a reviewer reading the class declaration would ever notice.
        IChatTool[] writeTools = [CompleteTool(), MarkPaidTool(), SendEmailTool(), DeleteTool()];

        writeTools.ShouldAllBe(tool => tool is IConfirmableChatTool);
        GetTool().ShouldNotBeAssignableTo<IConfirmableChatTool>();
    }

    /// <summary>The four lifecycle writes and the status each of them accepts.</summary>
    public static TheoryData<string, EInvoiceStatus> LifecycleWrites => new()
    {
        { "complete_invoice", EInvoiceStatus.Draft },
        { "mark_invoice_paid", EInvoiceStatus.Completed },
        { "send_invoice_email", EInvoiceStatus.Completed },
        { "delete_invoice", EInvoiceStatus.Draft }
    };

    /// <summary>
    /// One executor holding all four writes, the way DI wires them in production.
    /// <see cref="SendParameters"/> serves every one of them: the identity keys are shared and
    /// a recipient_email the other schemas do not declare is simply ignored.
    /// </summary>
    private ChatToolExecutor ExecutorOverEveryLifecycleWrite()
        => new([CompleteTool(), MarkPaidTool(), SendEmailTool(), DeleteTool()],
            Substitute.For<ILogger<ChatToolExecutor>>());

    [Theory]
    [MemberData(nameof(LifecycleWrites))]
    public async Task EveryLifecycleWrite_WithoutConfirm_OnlyPreviews(string toolName, EInvoiceStatus status)
    {
        // The type assertion above proves the tools declare the interface; this proves the
        // executor really holds each of them back. Only delete_invoice was walked through the
        // real seam before, and "confirmable in theory" is the state that ships a silent write.
        GivenInvoice(BuildInvoice(status: status));

        var result = await ExecutorOverEveryLifecycleWrite().ExecuteToolAsync(
            new ParsedToolCall { Action = toolName, Parameters = SendParameters() });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeTrue();
        result.OutputText.ShouldContain("NOTHING HAS BEEN CHANGED YET");
        await ShouldHaveWrittenNothing();
    }

    [Theory]
    [MemberData(nameof(LifecycleWrites))]
    public async Task EveryLifecycleWrite_WithConfirm_ReachesItsService(string toolName, EInvoiceStatus status)
    {
        GivenInvoice(BuildInvoice(status: status));
        GivenEveryWriteSucceeds();

        var parameters = SendParameters();
        parameters["confirm"] = "true";

        var result = await ExecutorOverEveryLifecycleWrite().ExecuteToolAsync(
            new ParsedToolCall { Action = toolName, Parameters = parameters });

        result.IsSuccess.ShouldBeTrue();
        result.RequiresConfirmation.ShouldBeFalse();
        await ShouldHaveWritten(toolName);
    }

    [Fact]
    public async Task SendInvoiceEmail_ThroughTheExecutor_WithoutARecipient_NeverReachesTheTool()
    {
        // The tool reads recipient_email through the indexer, which is only safe because central
        // validation rejects the call first — and a rejected call never ran, so the flag says so.
        GivenInvoice(BuildInvoice(status: EInvoiceStatus.Completed));

        var result = await ExecutorOver(SendEmailTool()).ExecuteToolAsync(
            new ParsedToolCall { Action = "send_invoice_email", Parameters = ById() });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("recipient_email");
        result.RequiresConfirmation.ShouldBeTrue();
        await _emailService.DidNotReceive()
            .SendInvoiceEmailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteInvoice_ThroughTheExecutor_WhenTheServiceThrows_StaysAnExecutedCall()
    {
        // InvoiceService.CompleteInvoiceAsync throws when the company settings are incomplete
        // (readiness gate, #206) or the variable symbol collides — neither is visible to the
        // preview, so the failure arrives from the write. It DID run: the model must not be told
        // nothing happened, because part of the change may already be persisted.
        GivenInvoice(BuildInvoice());
        _invoiceService.CompleteInvoiceAsync(42, Arg.Any<CancellationToken>())
            .Returns<Task<InvoiceDto?>>(_ => throw new InvalidOperationException("Company settings are incomplete"));

        var parameters = ById();
        parameters["confirm"] = "true";

        var result = await ExecutorOver(CompleteTool()).ExecuteToolAsync(
            new ParsedToolCall { Action = "complete_invoice", Parameters = parameters });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("Company settings are incomplete");
        result.RequiresConfirmation.ShouldBeFalse();
    }

    /// <summary>Every write the four tools can perform, all stubbed as successful.</summary>
    private void GivenEveryWriteSucceeds()
    {
        _invoiceService.CompleteInvoiceAsync(42, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EInvoiceStatus.Completed));
        _invoiceService.MarkAsPaidAsync(42, null, Arg.Any<CancellationToken>())
            .Returns(BuildInvoice(status: EInvoiceStatus.Paid));
        _invoiceService.DeleteInvoiceAsync(42, Arg.Any<CancellationToken>()).Returns(true);
    }

    /// <summary>Not one of the four writes reached a service — the whole point of the gate.</summary>
    private async Task ShouldHaveWrittenNothing()
    {
        await _invoiceService.DidNotReceive()
            .CompleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _invoiceService.DidNotReceive()
            .MarkAsPaidAsync(Arg.Any<long>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>());
        await _invoiceService.DidNotReceive()
            .DeleteInvoiceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive()
            .SendInvoiceEmailAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The confirmed tool performed its own write, on the invoice it resolved.</summary>
    private async Task ShouldHaveWritten(string toolName)
    {
        switch (toolName)
        {
            case "complete_invoice":
                await _invoiceService.Received(1).CompleteInvoiceAsync(42, Arg.Any<CancellationToken>());
                break;
            case "mark_invoice_paid":
                await _invoiceService.Received(1).MarkAsPaidAsync(42, null, Arg.Any<CancellationToken>());
                break;
            case "send_invoice_email":
                await _emailService.Received(1)
                    .SendInvoiceEmailAsync(42, "ucetni@alza.cz", Arg.Any<CancellationToken>());
                break;
            case "delete_invoice":
                await _invoiceService.Received(1).DeleteInvoiceAsync(42, Arg.Any<CancellationToken>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(toolName), toolName, "Unknown lifecycle write.");
        }
    }
}
