using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// The document process is identical for every document type (DEVGUIDE §5.6): one state machine
/// (<c>DocumentTransitions</c>) and one payment-status rule, shared by InvoiceService, the ReceivedInvoiceService
/// facade and PaymentMatchingService. These tests run the same scenarios for an ISSUED invoice and a RECEIVED
/// invoice and pin that the two differ only by the explicit type policy.
///
/// Every test seeds through its OWN DbContext and runs the service under test on ANOTHER one (same in-memory
/// store, different instance), then asserts through a third. Seeding through the service's context would let EF
/// fix-up hide a missing .Include() (known repo trap: false-green tests).
/// </summary>
public class DocumentStateMachineTests
{
    public enum Side { Issued, Received }

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly INumberSequenceService _numbers = Substitute.For<INumberSequenceService>();
    private int _seq;

    public DocumentStateMachineTests()
    {
        using var db = NewDb();
        db.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kc", DecimalPlaces = 2, SortOrder = 1, IsActive = true });
        db.Client.Add(new Client { Id = 1, CompanyName = "Us", RegistrationNumber = "11111111", IsIssuer = true, IsActive = true });
        db.Client.Add(new Client { Id = 2, CompanyName = "Counterparty", RegistrationNumber = "22222222", IsActive = true });
        db.BankAccount.Add(new BankAccount { Id = 1, ClientId = 1, AccountNumber = "1000000000/0100", CurrencyCode = "CZK", IsDefault = true });
        db.SaveChanges();
    }

    // ─── Plumbing: a fresh context + service per action ──────────────────────

    private TenantDbContext NewDb() => new(new DbContextOptionsBuilder<TenantDbContext>()
        .UseInMemoryDatabase(_dbName).Options);

    private InvoiceService Issued(TenantDbContext db) => new(db, _numbers,
        Substitute.For<ITenantReadinessService>(), NullLogger<InvoiceService>.Instance);

    private static ReceivedInvoiceService Received(TenantDbContext db) =>
        new(db, NullLogger<ReceivedInvoiceService>.Instance);

    private PaymentMatchingService Matching(TenantDbContext db) => new(db, Substitute.For<INotificationService>(),
        Issued(db), NullLogger<PaymentMatchingService>.Instance);

    private static EDocumentType TypeOf(Side side) => side == Side.Issued ? EDocumentType.Invoice : EDocumentType.ReceivedInvoice;

    private long SeedDoc(Side side, EInvoiceStatus status, decimal paidAmount = 0m, string? vs = null)
    {
        using var db = NewDb();
        var n = ++_seq;
        var number = side == Side.Issued ? $"2026{n:000}" : $"SUP-{n}";
        var doc = new Invoice
        {
            DocumentType = TypeOf(side), Status = status, DocumentNumber = number, ClientId = 2, IssuerId = 1, CurrencyId = 1,
            VariableSymbol = vs ?? $"{n:0000000000}", IssueDate = DateTime.UtcNow.AddDays(-30), DueDate = DateTime.UtcNow.AddDays(-10),
            TaxableSupplyDate = DateTime.UtcNow.AddDays(-30), ReceivedDate = side == Side.Received ? DateTime.UtcNow : null,
            TotalBeforeVat = 1000m, TotalVat = 210m, TotalWithVat = 1210m, PaidAmount = paidAmount,
            PaidAt = paidAmount >= 1210m ? DateTime.UtcNow : null
        };
        db.Invoice.Add(doc);
        db.SaveChanges();
        return doc.Id;
    }

    private long SeedTx(Side side, decimal amount, string? vs = null, EPaymentDirection? direction = null)
    {
        using var db = NewDb();
        var tx = new BankTransaction
        {
            BankAccountId = 1, Amount = amount, CurrencyCode = "CZK", VariableSymbol = vs,
            // We receive money for issued invoices and pay supplier invoices.
            Direction = direction ?? (side == Side.Issued ? EPaymentDirection.Incoming : EPaymentDirection.Outgoing),
            MatchStatus = EMatchStatus.Unmatched, TransactionDate = DateTime.UtcNow, DeduplicationHash = Guid.NewGuid().ToString("N")
        };
        db.BankTransaction.Add(tx);
        db.SaveChanges();
        return tx.Id;
    }

    private Invoice Load(long id)
    {
        using var db = NewDb();
        return db.Invoice.AsNoTracking().Single(i => i.Id == id);
    }

    private async Task ConfirmAsync(Side side, long txId, long docId)
    {
        using var db = NewDb();
        await Matching(db).ConfirmAutoMatchAsync(txId, side == Side.Issued ? docId : null, side == Side.Received ? docId : null, null);
    }

    /// <summary>One entry point per transition and side - the very thing under test is that both sides behave alike.</summary>
    private async Task RunAsync(Side side, string transition, long id)
    {
        using var db = NewDb();
        if (side == Side.Issued)
        {
            var svc = Issued(db);
            switch (transition)
            {
                case "complete": await svc.CompleteInvoiceAsync(id); break;
                case "pay": await svc.MarkAsPaidAsync(id); break;
                case "unpay": await svc.MarkAsUnpaidAsync(id); break;
                case "revert": await svc.RevertToDraftAsync(id); break;
                case "delete": await svc.DeleteInvoiceAsync(id); break;
                case "restore": await svc.RestoreInvoiceAsync(id); break;
                default: throw new ArgumentOutOfRangeException(nameof(transition));
            }
        }
        else
        {
            var svc = Received(db);
            switch (transition)
            {
                case "complete": await svc.ApproveAsync(id); break;
                case "pay": await svc.MarkAsPaidAsync(id); break;
                case "unpay": await svc.MarkAsUnpaidAsync(id); break;
                case "revert": await svc.RevertToDraftAsync(id); break;
                case "delete": await svc.DeleteAsync(id); break;
                case "restore": await svc.RestoreAsync(id); break;
                default: throw new ArgumentOutOfRangeException(nameof(transition));
            }
        }
    }

    // ─── The whole transition matrix, both sides ─────────────────────────────

    private static readonly (string Transition, EInvoiceStatus[] From, EInvoiceStatus To)[] Matrix =
    [
        ("complete", [EInvoiceStatus.Draft], EInvoiceStatus.Completed),
        ("pay", [EInvoiceStatus.Completed, EInvoiceStatus.PartiallyPaid], EInvoiceStatus.Paid),
        ("unpay", [EInvoiceStatus.Paid, EInvoiceStatus.PartiallyPaid], EInvoiceStatus.Completed),
        ("revert", [EInvoiceStatus.Completed], EInvoiceStatus.Draft),
        ("delete", [EInvoiceStatus.Draft], EInvoiceStatus.Deleted),
        ("restore", [EInvoiceStatus.Deleted], EInvoiceStatus.Draft),
    ];

    private static readonly EInvoiceStatus[] AllStatuses =
        [EInvoiceStatus.Draft, EInvoiceStatus.Completed, EInvoiceStatus.Paid, EInvoiceStatus.PartiallyPaid, EInvoiceStatus.Deleted];

    public static IEnumerable<object[]> MatrixCases()
    {
        foreach (var side in new[] { Side.Issued, Side.Received })
            foreach (var (transition, from, to) in Matrix)
                foreach (var status in AllStatuses)
                {
                    // Deleting a COMPLETED document is the one place where the type policy differs
                    // (issued: only the last one of the sequence; received: never) - see the policy tests below.
                    if (transition == "delete" && status == EInvoiceStatus.Completed) continue;
                    yield return [side, transition, status, from.Contains(status), to];
                }

        // Creditnoted exists on the issued side only (a received row can never reach it) and is a dead end:
        // every one of the six transitions is refused from it.
        foreach (var (transition, _, to) in Matrix)
            yield return [Side.Issued, transition, EInvoiceStatus.Creditnoted, false, to];
    }

    [Theory]
    [MemberData(nameof(MatrixCases))]
    public async Task Transition_IsAllowedExactlyFromItsSourceStatuses_ForBothSides(
        Side side, string transition, EInvoiceStatus from, bool allowed, EInvoiceStatus to)
    {
        var id = SeedDoc(side, from, paidAmount: from is EInvoiceStatus.Paid ? 1210m : from is EInvoiceStatus.PartiallyPaid ? 400m : 0m);

        if (allowed)
        {
            await RunAsync(side, transition, id);
            Load(id).Status.ShouldBe(to);
        }
        else
        {
            var ex = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(side, transition, id));
            ex.Message.ShouldNotBeNullOrWhiteSpace();
            Load(id).Status.ShouldBe(from); // a refused transition changes nothing
        }
    }

    [Theory]
    [InlineData(Side.Issued)]
    [InlineData(Side.Received)]
    public async Task MarkUnpaid_ResetsPaidAmountAndPaidAt(Side side)
    {
        var id = SeedDoc(side, EInvoiceStatus.Paid, paidAmount: 1210m);

        await RunAsync(side, "unpay", id);

        var doc = Load(id);
        doc.PaidAmount.ShouldBe(0m);
        doc.PaidAt.ShouldBeNull();
    }

    // ─── The type policy: the ONLY differences ───────────────────────────────

    [Fact]
    public async Task Policy_Issued_CompletedDocument_CanBeDeletedOnlyWhenItIsTheLastOfItsType()
    {
        var first = SeedDoc(Side.Issued, EInvoiceStatus.Completed);
        var last = SeedDoc(Side.Issued, EInvoiceStatus.Completed);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(Side.Issued, "delete", first));
        ex.Message.ShouldContain("last completed invoice");

        await RunAsync(Side.Issued, "delete", last);
        Load(last).Status.ShouldBe(EInvoiceStatus.Deleted);
    }

    [Fact]
    public async Task Policy_Received_CompletedDocument_CanNeverBeDeleted_RevertToDraftFirst()
    {
        var id = SeedDoc(Side.Received, EInvoiceStatus.Completed);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(Side.Received, "delete", id));
        ex.Message.ShouldContain("revert it to draft");

        await RunAsync(Side.Received, "revert", id);
        await RunAsync(Side.Received, "delete", id);
        Load(id).Status.ShouldBe(EInvoiceStatus.Deleted);
    }

    [Fact]
    public async Task Policy_Issued_Delete_ClearsTheNumberAndReleasesItToTheSequence()
    {
        using (var seed = NewDb())
        {
            seed.NumberSequence.Add(new NumberSequence { Name = "Invoices", DocumentType = EDocumentType.Invoice, IsDefault = true, IsActive = true, CurrentNumber = 5 });
            seed.SaveChanges();
        }
        var id = SeedDoc(Side.Issued, EInvoiceStatus.Draft);

        await RunAsync(Side.Issued, "delete", id);

        Load(id).DocumentNumber.ShouldBeNull();
        await _numbers.Received(1).TryReleaseLastNumberAsync(EDocumentType.Invoice, 5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Policy_Received_NumberIsTheSuppliers_NeverClearedByAnyTransition()
    {
        var id = SeedDoc(Side.Received, EInvoiceStatus.Draft);
        var number = Load(id).DocumentNumber;

        foreach (var transition in new[] { "complete", "pay", "unpay", "revert", "delete", "restore" })
        {
            await RunAsync(Side.Received, transition, id);
            Load(id).DocumentNumber.ShouldBe(number, $"after '{transition}'");
        }

        // Received documents never touch the number sequence.
        await _numbers.DidNotReceiveWithAnyArgs().TryReleaseLastNumberAsync(default, default, default);
    }

    // ─── Original bug: a PAID received invoice had no way back ───────────────

    [Fact]
    public async Task Received_PaidManually_CanGoAllTheWayBack_ToDeletedAndRestored()
    {
        var id = SeedDoc(Side.Received, EInvoiceStatus.Draft);

        await RunAsync(Side.Received, "complete", id);
        await RunAsync(Side.Received, "pay", id);
        Load(id).Status.ShouldBe(EInvoiceStatus.Paid);

        await RunAsync(Side.Received, "unpay", id);
        await RunAsync(Side.Received, "revert", id);
        await RunAsync(Side.Received, "delete", id);
        Load(id).Status.ShouldBe(EInvoiceStatus.Deleted);

        await RunAsync(Side.Received, "restore", id);
        Load(id).Status.ShouldBe(EInvoiceStatus.Draft);

        using var db = NewDb();
        (await Received(db).GetByIdAsync(id))!.Status.ShouldBe(EReceivedInvoiceStatus.Received);
    }

    [Fact]
    public async Task Received_PaidByBankPayment_MarkUnpaidRemovesMatchesAndRecalculatesTransactions()
    {
        var id = SeedDoc(Side.Received, EInvoiceStatus.Completed);
        var otherId = SeedDoc(Side.Received, EInvoiceStatus.Completed);
        var tx = SeedTx(Side.Received, 600m);
        var sharedTx = SeedTx(Side.Received, 2000m); // pays our document (1210) AND another one (790)
        await ConfirmAsync(Side.Received, tx, id);
        await ConfirmAsync(Side.Received, sharedTx, id);
        using (var seed = NewDb())
        {
            // The shared transaction also covers another document, which has to survive the mark-unpaid.
            seed.PaymentMatch.Add(new PaymentMatch { BankTransactionId = sharedTx, InvoiceId = otherId, MatchedAmount = 790m, MatchedAt = DateTime.UtcNow });
            seed.SaveChanges();
        }
        Load(id).Status.ShouldBe(EInvoiceStatus.Paid);

        await RunAsync(Side.Received, "unpay", id);

        using var check = NewDb();
        check.PaymentMatch.Count(m => m.InvoiceId == id).ShouldBe(0);
        check.PaymentMatch.Count(m => m.InvoiceId == otherId).ShouldBe(1);
        check.BankTransaction.Single(t => t.Id == tx).MatchStatus.ShouldBe(EMatchStatus.Unmatched);
        check.BankTransaction.Single(t => t.Id == sharedTx).MatchStatus.ShouldBe(EMatchStatus.PartiallyMatched);
        var doc = Load(id);
        doc.Status.ShouldBe(EInvoiceStatus.Completed);
        doc.PaidAmount.ShouldBe(0m);

        await RunAsync(Side.Received, "revert", id);
        await RunAsync(Side.Received, "delete", id);
        await RunAsync(Side.Received, "restore", id);
        Load(id).Status.ShouldBe(EInvoiceStatus.Draft);
    }

    // ─── Payments: same rules for both sides ─────────────────────────────────

    [Theory]
    [InlineData(Side.Issued)]
    [InlineData(Side.Received)]
    public async Task Payments_PartialThenFull_ThenUnmatchOneOfTwo_ThenAll(Side side)
    {
        var id = SeedDoc(side, EInvoiceStatus.Completed);
        var tx1 = SeedTx(side, 400m);
        var tx2 = SeedTx(side, 810m);

        await ConfirmAsync(side, tx1, id);
        var partial = Load(id);
        partial.Status.ShouldBe(EInvoiceStatus.PartiallyPaid);
        partial.PaidAmount.ShouldBe(400m);
        partial.PaidAt.ShouldBeNull();

        await ConfirmAsync(side, tx2, id);
        var paid = Load(id);
        paid.Status.ShouldBe(EInvoiceStatus.Paid);
        paid.PaidAmount.ShouldBe(1210m);

        long match1, match2;
        using (var db = NewDb())
        {
            match1 = db.PaymentMatch.Single(m => m.BankTransactionId == tx1).Id;
            match2 = db.PaymentMatch.Single(m => m.BankTransactionId == tx2).Id;
        }

        using (var db = NewDb()) await Matching(db).UnmatchAsync(match2, null, null);
        var back = Load(id);
        back.Status.ShouldBe(EInvoiceStatus.PartiallyPaid);
        back.PaidAmount.ShouldBe(400m);
        back.PaidAt.ShouldBeNull();

        using (var db = NewDb()) await Matching(db).UnmatchAsync(match1, null, null);
        var none = Load(id);
        none.Status.ShouldBe(EInvoiceStatus.Completed);
        none.PaidAmount.ShouldBe(0m);
    }

    [Fact]
    public async Task Received_PartiallyPaid_IsStillApprovedInTheLegacyDto()
    {
        var id = SeedDoc(Side.Received, EInvoiceStatus.Completed);
        await ConfirmAsync(Side.Received, SeedTx(Side.Received, 400m), id);

        using var db = NewDb();
        (await Received(db).GetByIdAsync(id))!.Status.ShouldBe(EReceivedInvoiceStatus.Approved);
    }

    [Theory]
    [InlineData(Side.Issued)]
    [InlineData(Side.Received)]
    public async Task Unmatch_OnDeletedDocument_NeverRevivesIt(Side side)
    {
        // A legacy leftover: a deleted document that still carries a payment.
        var id = SeedDoc(side, EInvoiceStatus.Deleted, paidAmount: 400m);
        var tx = SeedTx(side, 400m);
        long matchId;
        using (var seed = NewDb())
        {
            var match = new PaymentMatch { BankTransactionId = tx, InvoiceId = id, MatchedAmount = 400m, MatchedAt = DateTime.UtcNow };
            seed.PaymentMatch.Add(match);
            seed.SaveChanges();
            matchId = match.Id;
        }

        using (var db = NewDb()) await Matching(db).UnmatchAsync(matchId, null, null);

        var doc = Load(id);
        doc.Status.ShouldBe(EInvoiceStatus.Deleted);
        doc.PaidAmount.ShouldBe(0m);
    }

    [Theory]
    [InlineData(Side.Issued, EInvoiceStatus.Completed, true)]
    [InlineData(Side.Issued, EInvoiceStatus.Deleted, false)]
    [InlineData(Side.Issued, EInvoiceStatus.Draft, false)]
    [InlineData(Side.Received, EInvoiceStatus.Completed, true)]
    [InlineData(Side.Received, EInvoiceStatus.Deleted, false)]
    [InlineData(Side.Received, EInvoiceStatus.Draft, false)] // not approved yet (legacy "Received")
    public async Task MatchCandidates_ExcludeDeletedAndDraftDocuments(Side side, EInvoiceStatus status, bool expectMatch)
    {
        var id = SeedDoc(side, status, vs: "7770001");
        var tx = SeedTx(side, 1210m, vs: "7770001");

        using var db = NewDb();
        var matching = Matching(db);

        // Proposal for the document...
        var proposal = side == Side.Issued
            ? await matching.FindAutoMatchForInvoiceAsync(id)
            : await matching.FindAutoMatchForReceivedInvoiceAsync(id);
        (proposal != null).ShouldBe(expectMatch);

        // ...proposal for the transaction (it only handles INCOMING ones; a supplier invoice is offered to an incoming
        // transaction when no issued invoice fits)...
        var incomingTx = side == Side.Issued ? tx : SeedTx(side, 1210m, vs: "7770001", direction: EPaymentDirection.Incoming);
        var forTx = await matching.FindAutoMatchForTransactionAsync(incomingTx);
        (forTx != null).ShouldBe(expectMatch);

        // ...and the automatic matcher itself.
        await matching.MatchAsync(tx);
        using var check = NewDb();
        check.PaymentMatch.Any(m => m.InvoiceId == id).ShouldBe(expectMatch);
        Load(id).Status.ShouldBe(expectMatch ? EInvoiceStatus.Paid : status);
    }

    [Theory]
    [InlineData(Side.Issued, EInvoiceStatus.Deleted)]
    [InlineData(Side.Received, EInvoiceStatus.Deleted)]
    [InlineData(Side.Issued, EInvoiceStatus.Draft)]
    [InlineData(Side.Received, EInvoiceStatus.Draft)] // must be approved first
    public async Task ConfirmMatch_OnDeletedOrDraftDocument_IsRefused(Side side, EInvoiceStatus status)
    {
        var id = SeedDoc(side, status);
        var tx = SeedTx(side, 1210m);

        await Should.ThrowAsync<InvalidOperationException>(() => ConfirmAsync(side, tx, id));

        var doc = Load(id);
        doc.Status.ShouldBe(status);
        doc.PaidAmount.ShouldBe(0m);
        using var check = NewDb();
        check.PaymentMatch.Any(m => m.InvoiceId == id).ShouldBeFalse();
    }

    [Fact]
    public async Task ManualMatch_OnDraftIssuedDocument_IsRefused()
    {
        // The manual dialog exists for issued documents only; a Draft must be completed first.
        var id = SeedDoc(Side.Issued, EInvoiceStatus.Draft);
        var tx = SeedTx(Side.Issued, 1210m);

        using (var db = NewDb())
            await Should.ThrowAsync<InvalidOperationException>(() => Matching(db).ManualMatchAsync(tx, id, 1210m, null, null));

        Load(id).Status.ShouldBe(EInvoiceStatus.Draft);
        using var check = NewDb();
        check.PaymentMatch.Any(m => m.InvoiceId == id).ShouldBeFalse();
    }

    [Fact]
    public async Task Received_Draft_BecomesMatchableOnceApproved()
    {
        var id = SeedDoc(Side.Received, EInvoiceStatus.Draft, vs: "7770002");
        var tx = SeedTx(Side.Received, 1210m, vs: "7770002");

        using (var db = NewDb()) await Matching(db).MatchAsync(tx);
        Load(id).Status.ShouldBe(EInvoiceStatus.Draft);
        using (var check = NewDb())
            check.BankTransaction.Single(t => t.Id == tx).MatchStatus.ShouldBe(EMatchStatus.Unmatched);

        await RunAsync(Side.Received, "complete", id);
        using (var db = NewDb()) await Matching(db).MatchAsync(tx);

        Load(id).Status.ShouldBe(EInvoiceStatus.Paid);
    }

    // ─── Unmatch after a manual top-up is refused (owner decision) ───────────

    [Theory]
    [InlineData(Side.Issued)]
    [InlineData(Side.Received)]
    public async Task Unmatch_AfterManualTopUp_IsRefusedAndChangesNothing_MarkUnpaidThenCleansEverything(Side side)
    {
        // 1210 total: 600 came from the bank, the rest was marked as paid by hand.
        var id = SeedDoc(side, EInvoiceStatus.Completed);
        var tx = SeedTx(side, 600m);
        await ConfirmAsync(side, tx, id);
        Load(id).Status.ShouldBe(EInvoiceStatus.PartiallyPaid);
        await RunAsync(side, "pay", id);
        var paid = Load(id);
        paid.Status.ShouldBe(EInvoiceStatus.Paid);
        paid.PaidAmount.ShouldBe(1210m);

        long matchId;
        using (var db = NewDb()) matchId = db.PaymentMatch.Single(m => m.InvoiceId == id).Id;

        // Unmatching the 600 would recompute PaidAmount to 0 and silently lose the manual 610 -> refused.
        InvalidOperationException ex;
        using (var db = NewDb())
            ex = await Should.ThrowAsync<InvalidOperationException>(() => Matching(db).UnmatchAsync(matchId, null, null));
        ex.Message.ShouldContain("mark it as unpaid");

        var untouched = Load(id);
        untouched.Status.ShouldBe(EInvoiceStatus.Paid);
        untouched.PaidAmount.ShouldBe(1210m);
        untouched.PaidAt.ShouldBe(paid.PaidAt);
        using (var check = NewDb())
        {
            check.PaymentMatch.Count(m => m.InvoiceId == id).ShouldBe(1);
            check.BankTransaction.Single(t => t.Id == tx).MatchStatus.ShouldBe(EMatchStatus.Matched);
        }

        // The way out the message points to: cancel the payment on the document.
        await RunAsync(side, "unpay", id);

        var clean = Load(id);
        clean.Status.ShouldBe(EInvoiceStatus.Completed);
        clean.PaidAmount.ShouldBe(0m);
        clean.PaidAt.ShouldBeNull();
        using (var check = NewDb())
        {
            check.PaymentMatch.Any(m => m.InvoiceId == id).ShouldBeFalse();
            check.BankTransaction.Single(t => t.Id == tx).MatchStatus.ShouldBe(EMatchStatus.Unmatched);
        }
    }

    [Theory]
    [InlineData(Side.Issued)]
    [InlineData(Side.Received)]
    public async Task Unmatch_OfOneOfTwoMatches_AfterManualTopUp_IsRefusedToo(Side side)
    {
        // Two bank payments (300 + 300) and a manual rest: no single match may be taken out.
        var id = SeedDoc(side, EInvoiceStatus.Completed);
        await ConfirmAsync(side, SeedTx(side, 300m), id);
        await ConfirmAsync(side, SeedTx(side, 300m), id);
        await RunAsync(side, "pay", id);

        long[] matchIds;
        using (var db = NewDb()) matchIds = db.PaymentMatch.Where(m => m.InvoiceId == id).Select(m => m.Id).ToArray();

        foreach (var matchId in matchIds)
        {
            using var db = NewDb();
            await Should.ThrowAsync<InvalidOperationException>(() => Matching(db).UnmatchAsync(matchId, null, null));
        }

        Load(id).PaidAmount.ShouldBe(1210m);
        using var check = NewDb();
        check.PaymentMatch.Count(m => m.InvoiceId == id).ShouldBe(2);
    }

    [Fact]
    public async Task ReceivedProposal_ForPartiallyPaidDocument_ReportsWhatIsAlreadyPaid()
    {
        // 1210 total, 400 already matched: the dialog must show Paid 400 / Remaining 810, not Paid 0.
        var id = SeedDoc(Side.Received, EInvoiceStatus.Completed, vs: "7770003");
        await ConfirmAsync(Side.Received, SeedTx(Side.Received, 400m), id);
        var incomingTx = SeedTx(Side.Received, 810m, vs: "7770003", direction: EPaymentDirection.Incoming);

        using var db = NewDb();
        var proposal = await Matching(db).FindAutoMatchForTransactionAsync(incomingTx);

        proposal.ShouldNotBeNull();
        proposal.ReceivedInvoiceId.ShouldBe(id);
        proposal.TotalWithVat.ShouldBe(1210m);
        proposal.PaidAmount.ShouldBe(400m);
        proposal.Remaining.ShouldBe(810m);
    }

    [Fact]
    public async Task Unmatch_OnCreditnotedInvoice_NeverReopensIt()
    {
        // A paid invoice was credit-noted; its bank payment is unmatched afterwards.
        var id = SeedDoc(Side.Issued, EInvoiceStatus.Creditnoted, paidAmount: 1210m);
        var tx = SeedTx(Side.Issued, 1210m);
        long matchId;
        using (var seed = NewDb())
        {
            var match = new PaymentMatch { BankTransactionId = tx, InvoiceId = id, MatchedAmount = 1210m, MatchedAt = DateTime.UtcNow };
            seed.PaymentMatch.Add(match);
            seed.SaveChanges();
            matchId = match.Id;
        }

        using (var db = NewDb()) await Matching(db).UnmatchAsync(matchId, null, null);

        var doc = Load(id);
        doc.Status.ShouldBe(EInvoiceStatus.Creditnoted); // not Completed
        doc.PaidAmount.ShouldBe(0m);
        using var check = NewDb();
        check.PaymentMatch.Any(m => m.InvoiceId == id).ShouldBeFalse();
        check.BankTransaction.Single(t => t.Id == tx).MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }
}
