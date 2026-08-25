using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Service.ChatTools;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for the two payment chat tools (issue #227):
///   - ListPaymentsTool (read-only)
///   - GetPaymentTool   (read-only)
///
/// Both are read-only by decision of story #149 — matching a payment to an invoice stays in the
/// UI — so there is no confirm gate to test here. What is verified is what the tools own:
/// parameter parsing, the filter they hand to <see cref="IBankTransactionQueryService"/>, the
/// error paths, and that an outgoing payment cannot read as income.
/// </summary>
public class PaymentChatToolTests
{
    // ─── Shared builders ──────────────────────────────────────────────────

    private static BankTransactionDto BuildPayment(
        long id = 7,
        EPaymentDirection direction = EPaymentDirection.Incoming,
        EMatchStatus matchStatus = EMatchStatus.Unmatched,
        string[]? matchedInvoiceNumbers = null)
        => new()
        {
            Id = id,
            BankAccountId = 3,
            BankAccountLabel = "CZK účet — Fio",
            TransactionDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc),
            Amount = 12_100m,
            CurrencyCode = "CZK",
            Direction = direction,
            VariableSymbol = "20260042",
            CounterpartyName = "ABC s.r.o.",
            CounterpartyAccount = "1234567890/0100",
            Message = "Platba faktury 2026-0042",
            ImportSource = EImportSource.Manual,
            MatchStatus = matchStatus,
            MatchedInvoiceNumbers = [.. matchedInvoiceNumbers ?? []],
            MatchedTotal = matchedInvoiceNumbers is { Length: > 0 } ? 12_100m : 0m
        };

    /// <summary>Query service stub that returns one page with the supplied rows.</summary>
    private static IBankTransactionQueryService StubList(params BankTransactionDto[] payments)
    {
        var service = Substitute.For<IBankTransactionQueryService>();
        service.ListAsync(Arg.Any<BankTransactionFilterDto>(), Arg.Any<PaginationParams>(), Arg.Any<CancellationToken>())
            .Returns(new PagedResult<BankTransactionDto>([.. payments], payments.Length, 1, 10));
        return service;
    }

    private static ListPaymentsTool CreateListTool(IBankTransactionQueryService service)
        => new(service, Substitute.For<ILogger<ListPaymentsTool>>());

    private static GetPaymentTool CreateGetTool(IBankTransactionQueryService service)
        => new(service, Substitute.For<ILogger<GetPaymentTool>>());

    /// <summary>Pulls the filter and paging the list tool handed to the service.</summary>
    private static (BankTransactionFilterDto Filter, PaginationParams Paging) CapturedQuery(
        IBankTransactionQueryService service)
    {
        var arguments = service.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IBankTransactionQueryService.ListAsync))
            .GetArguments();

        return ((BankTransactionFilterDto)arguments[0]!, (PaginationParams)arguments[1]!);
    }

    // ─── list_payments ────────────────────────────────────────────────────

    [Fact]
    public async Task ListPayments_WithoutParameters_UsesFirstPageAndNoFilters()
    {
        var service = StubList(BuildPayment());

        var result = await CreateListTool(service).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();

        var (filter, paging) = CapturedQuery(service);
        paging.Page.ShouldBe(1);
        paging.PageSize.ShouldBe(10);
        filter.Status.ShouldBeNull();
        filter.Direction.ShouldBeNull();
        filter.Search.ShouldBeNull();
        filter.From.ShouldBeNull();
        filter.To.ShouldBeNull();
    }

    [Fact]
    public async Task ListPayments_MapsEveryParameterOntoTheFilter()
    {
        var service = StubList(BuildPayment());

        await CreateListTool(service).ExecuteAsync(new Dictionary<string, string>
        {
            ["status"] = "unmatched",     // lower case on purpose — models are inconsistent about casing
            ["direction"] = "Outgoing",
            ["search"] = "  ABC  ",
            ["date_from"] = "2026-03-01",
            ["date_to"] = "15.3.2026",    // Czech form a user dictated and the model forwarded
            ["page"] = "2",
            ["page_size"] = "25"
        });

        var (filter, paging) = CapturedQuery(service);
        filter.Status.ShouldBe(EMatchStatus.Unmatched);
        filter.Direction.ShouldBe(EPaymentDirection.Outgoing);
        filter.Search.ShouldBe("ABC");
        filter.From.ShouldBe(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        filter.To.ShouldBe(new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc));
        paging.Page.ShouldBe(2);
        paging.PageSize.ShouldBe(25);

        // Npgsql rejects any other kind against 'timestamp with time zone' columns.
        filter.From!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact]
    public async Task ListPayments_ClampsAnAbsurdPageSize()
    {
        var service = StubList(BuildPayment());

        await CreateListTool(service).ExecuteAsync(new Dictionary<string, string> { ["page_size"] = "5000" });

        // The clamp lives in PaginationParams; the tool must not defeat it by assigning blindly.
        CapturedQuery(service).Paging.PageSize.ShouldBe(100);
    }

    [Fact]
    public async Task ListPayments_WithUnreadableDate_FailsInsteadOfDroppingTheFilter()
    {
        var service = StubList(BuildPayment());

        var result = await CreateListTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["date_to"] = "yesterday" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("date_to");

        await service.DidNotReceive().ListAsync(
            Arg.Any<BankTransactionFilterDto>(), Arg.Any<PaginationParams>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListPayments_WithNoMatches_SaysSoInsteadOfPrintingAnEmptyList()
    {
        var result = await CreateListTool(StubList()).ExecuteAsync([]);

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("No payments match");
    }

    [Fact]
    public async Task ListPayments_RendersIncomingMoneyWithAPlusSign()
    {
        var result = await CreateListTool(StubList(BuildPayment())).ExecuteAsync([]);

        result.OutputText.ShouldContain("ID=7");
        result.OutputText.ShouldContain("2026-03-15");
        result.OutputText.ShouldContain("+12,100.00 CZK");
        result.OutputText.ShouldContain("Unmatched");
        result.OutputText.ShouldContain("VS: 20260042");
    }

    [Fact]
    public async Task ListPayments_RendersOutgoingMoneyWithAMinusSign()
    {
        var payment = BuildPayment(direction: EPaymentDirection.Outgoing);

        var result = await CreateListTool(StubList(payment)).ExecuteAsync([]);

        // The amount column stores an absolute value — only the direction says which way it went.
        result.OutputText.ShouldContain("-12,100.00 CZK");
    }

    [Fact]
    public async Task ListPayments_NamesTheInvoiceAMatchedPaymentBelongsTo()
    {
        var payment = BuildPayment(matchStatus: EMatchStatus.Matched, matchedInvoiceNumbers: ["2026-0042"]);

        var result = await CreateListTool(StubList(payment)).ExecuteAsync([]);

        result.OutputText.ShouldContain("matched to: 2026-0042");
    }

    [Fact]
    public async Task ListPayments_NamesTheRecognizedCounterpartyWhenThereIsNoInvoice()
    {
        var payment = BuildPayment(matchStatus: EMatchStatus.Recognized);
        payment.RecognizedCounterpartyId = 4;
        payment.RecognizedCounterpartyLabel = "OSSZ — sociální pojištění";

        var result = await CreateListTool(StubList(payment)).ExecuteAsync([]);

        result.OutputText.ShouldContain("recognized as: OSSZ — sociální pojištění");
    }

    // ─── get_payment ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPayment_RendersTheWholeDetail()
    {
        var payment = BuildPayment(matchStatus: EMatchStatus.Matched, matchedInvoiceNumbers: ["2026-0042"]);
        var service = Substitute.For<IBankTransactionQueryService>();
        service.GetAsync(7, Arg.Any<CancellationToken>()).Returns(payment);

        var result = await CreateGetTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = "7" });

        result.IsSuccess.ShouldBeTrue();
        result.OutputText.ShouldContain("Payment ID: 7");
        result.OutputText.ShouldContain("Date: 2026-03-15");
        result.OutputText.ShouldContain("+12,100.00 CZK (money received)");
        result.OutputText.ShouldContain("Our bank account: CZK účet — Fio");
        result.OutputText.ShouldContain("Counterparty: ABC s.r.o.");
        result.OutputText.ShouldContain("Counterparty account: 1234567890/0100");
        result.OutputText.ShouldContain("Variable symbol: 20260042");
        result.OutputText.ShouldContain("Message: Platba faktury 2026-0042");
        result.OutputText.ShouldContain("Matching status: Matched");
        result.OutputText.ShouldContain("Matched to invoices: 2026-0042");
        result.OutputText.ShouldContain("Matched amount: 12,100.00 CZK out of 12,100.00 CZK");
    }

    [Fact]
    public async Task GetPayment_SkipsFieldsTheBankDidNotFillIn()
    {
        var payment = BuildPayment();
        payment.ConstantSymbol = null;
        payment.SpecificSymbol = "   ";
        payment.TransactionCode = null;

        var service = Substitute.For<IBankTransactionQueryService>();
        service.GetAsync(7, Arg.Any<CancellationToken>()).Returns(payment);

        var result = await CreateGetTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = "7" });

        result.OutputText.ShouldNotContain("Constant symbol");
        result.OutputText.ShouldNotContain("Specific symbol");
        result.OutputText.ShouldNotContain("Bank transaction code");
    }

    [Fact]
    public async Task GetPayment_WithUnknownId_SaysWhichIdWasNotFound()
    {
        var service = Substitute.For<IBankTransactionQueryService>();
        service.GetAsync(999, Arg.Any<CancellationToken>()).Returns((BankTransactionDto?)null);

        var result = await CreateGetTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = "999" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("999");
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public async Task GetPayment_WithANonNumericId_FailsWithoutQuerying(string id)
    {
        var service = Substitute.For<IBankTransactionQueryService>();

        // The executor validates the integer type before the tool runs; this guards direct calls.
        var result = await CreateGetTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = id });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("id");
        await service.DidNotReceive().GetAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetPayment_AcceptsAnIdThatArrivedPadded()
    {
        var service = Substitute.For<IBankTransactionQueryService>();
        service.GetAsync(7, Arg.Any<CancellationToken>()).Returns(BuildPayment());

        var result = await CreateGetTool(service)
            .ExecuteAsync(new Dictionary<string, string> { ["id"] = " 7 " });

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void PaymentTools_AreNotConfirmable_BecauseNeitherWrites()
    {
        var service = Substitute.For<IBankTransactionQueryService>();

        CreateListTool(service).ShouldNotBeAssignableTo<IConfirmableChatTool>();
        CreateGetTool(service).ShouldNotBeAssignableTo<IConfirmableChatTool>();
    }
}
