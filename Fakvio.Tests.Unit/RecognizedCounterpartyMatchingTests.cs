using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Recognition of transactions against the recognized-counterparty registry
/// (Rule 3 fallback in PaymentMatchingService) + assign/unassign/rescan flows.
/// Same in-memory DbContext approach as PaymentMatchingServiceTests.
/// </summary>
public class RecognizedCounterpartyMatchingTests : IDisposable
{
    private const string OsszAccount = "1011-7724311/0710";

    private readonly TenantDbContext _context;
    private readonly PaymentMatchingService _sut;

    private long _issuerId;
    private long _currencyCzkId;
    private long _bankAccountId;

    public RecognizedCounterpartyMatchingTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);
        _sut = new PaymentMatchingService(
            _context, Substitute.For<INotificationService>(), Substitute.For<ILogger<PaymentMatchingService>>());

        Seed();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Recognition during MatchAsync ────────────────────────────────────

    [Fact]
    public async Task OutgoingToKnownAccount_IsRecognized()
    {
        var entry = AddEntry("OSSZ", OsszAccount);
        var tx = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Recognized);
        reloaded.RecognizedCounterpartyId.ShouldBe(entry.Id);
    }

    [Fact]
    public async Task IncomingFromKnownAccount_IsRecognized()
    {
        // VAT refund arrives as an INCOMING payment from the tax office account.
        var entry = AddEntry("FÚ — DPH", "705-77628031/0710");
        var tx = AddTransaction(account: "705-77628031/0710", direction: EPaymentDirection.Incoming);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Recognized);
        reloaded.RecognizedCounterpartyId.ShouldBe(entry.Id);
    }

    [Fact]
    public async Task AccountComparison_IsNormalized()
    {
        // Entry stored with hyphen, transaction reported without it — must still match.
        AddEntry("OSSZ", OsszAccount);
        var tx = AddTransaction(account: "10117724311/0710", direction: EPaymentDirection.Outgoing);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Recognized);
    }

    [Fact]
    public async Task EntryWithVs_MatchesOnlyWhenVsEqual()
    {
        AddEntry("FÚ — DPH", OsszAccount, vs: "8809993333");

        var matching = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing, vs: "8809993333");
        var other = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing, vs: "1234");

        await _sut.MatchAsync(matching.Id);
        await _sut.MatchAsync(other.Id);

        (await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == matching.Id))
            .MatchStatus.ShouldBe(EMatchStatus.Recognized);
        // VS mismatch → entry does not apply → stays Unmatched.
        (await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == other.Id))
            .MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    [Fact]
    public async Task InvoiceVsRule_WinsOverRegistry()
    {
        // Same account is both a supplier's account on a received invoice AND a registry entry.
        var entry = AddEntry("Nájem", "555/0300");
        var ri = AddReceivedInvoice(vs: "999", total: 5000m, supplierAccount: "555/0300");
        var tx = AddTransaction(account: "555/0300", direction: EPaymentDirection.Outgoing, vs: "999", amount: 5000m);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        // Invoice match (VS rule) must win — transaction is Matched, not Recognized.
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Matched);
        reloaded.RecognizedCounterpartyId.ShouldBeNull();

        (await _context.ReceivedInvoice.AsNoTracking().FirstAsync(r => r.Id == ri.Id))
            .Status.ShouldBe(EReceivedInvoiceStatus.Paid);
        _ = entry; // silences unused warning — presence of the entry is the point
    }

    [Fact]
    public async Task MoreSpecificEntry_WinsOverAccountOnlyEntry()
    {
        var generic = AddEntry("FÚ — obecný", OsszAccount);
        var specific = AddEntry("FÚ — DPH", OsszAccount, vs: "8809993333");

        var tx = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing, vs: "8809993333");

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Recognized);
        reloaded.RecognizedCounterpartyId.ShouldBe(specific.Id);
        _ = generic;
    }

    [Fact]
    public async Task TwoEquallySpecificEntriesWithDifferentLabels_NeedsReview()
    {
        AddEntry("Label A", OsszAccount);
        AddEntry("Label B", OsszAccount);

        var tx = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.NeedsReview);
        reloaded.RecognizedCounterpartyId.ShouldBeNull();
    }

    [Fact]
    public async Task InactiveEntry_IsIgnored()
    {
        AddEntry("OSSZ", OsszAccount, isActive: false);
        var tx = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing);

        await _sut.MatchAsync(tx.Id);

        (await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id))
            .MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    [Fact]
    public async Task RecognizedTransaction_IsSkippedByMatchAsync()
    {
        var entry = AddEntry("OSSZ", OsszAccount);
        var tx = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing);
        await _sut.MatchAsync(tx.Id);

        // Second run must be a no-op (idempotent) — no exception, still Recognized.
        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Recognized);
        reloaded.RecognizedCounterpartyId.ShouldBe(entry.Id);
    }

    // ─── Manual match clears recognition ──────────────────────────────────

    [Fact]
    public async Task ManualMatch_ClearsRecognizedCounterparty()
    {
        var entry = AddEntry("OSSZ", OsszAccount);
        var tx = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing, amount: 1000m);
        await _sut.AssignRecognizedAsync(tx.Id, entry.Id, userId: null);

        var invoice = AddInvoice(vs: "555", total: 1000m);
        await _sut.ManualMatchAsync(tx.Id, invoice.Id, 1000m, note: null, userId: null);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Matched);
        reloaded.RecognizedCounterpartyId.ShouldBeNull();
    }

    // ─── Assign / Unassign ────────────────────────────────────────────────

    [Fact]
    public async Task AssignRecognized_SetsFkAndStatus()
    {
        var entry = AddEntry("VZP", "111/0100");
        var tx = AddTransaction(account: "999/0800", direction: EPaymentDirection.Outgoing);

        await _sut.AssignRecognizedAsync(tx.Id, entry.Id, userId: 7);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Recognized);
        reloaded.RecognizedCounterpartyId.ShouldBe(entry.Id);
    }

    [Fact]
    public async Task AssignRecognized_MatchedTransaction_Throws()
    {
        var entry = AddEntry("VZP", "111/0100");
        var invoice = AddInvoice(vs: "1", total: 100m);
        var tx = AddTransaction(account: null, direction: EPaymentDirection.Incoming, vs: "1", amount: 100m);
        await _sut.MatchAsync(tx.Id); // → Matched

        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AssignRecognizedAsync(tx.Id, entry.Id, userId: null));
    }

    [Fact]
    public async Task UnassignRecognized_RevertsToUnmatched()
    {
        var entry = AddEntry("OSSZ", OsszAccount);
        var tx = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing);
        await _sut.MatchAsync(tx.Id); // → Recognized

        await _sut.UnassignRecognizedAsync(tx.Id, userId: null);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
        reloaded.RecognizedCounterpartyId.ShouldBeNull();
    }

    // ─── Rescan ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RescanUnmatched_RecognizesExistingUnmatchedOnly()
    {
        // Historical transactions created BEFORE the registry entry exists.
        var unmatched = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing);
        var ignored = AddTransaction(account: OsszAccount, direction: EPaymentDirection.Outgoing);
        ignored.MatchStatus = EMatchStatus.Ignored;
        await _context.SaveChangesAsync();

        AddEntry("OSSZ", OsszAccount);

        var recognized = await _sut.RescanUnmatchedAsync();

        recognized.ShouldBe(1);
        (await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == unmatched.Id))
            .MatchStatus.ShouldBe(EMatchStatus.Recognized);
        // Ignored transactions must not be touched.
        (await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == ignored.Id))
            .MatchStatus.ShouldBe(EMatchStatus.Ignored);
    }

    // ─── Seed + builders ──────────────────────────────────────────────────

    private void Seed()
    {
        var czk = new Currency { Code = "CZK", Name = "Česká koruna", Symbol = "Kč", IsActive = true, SortOrder = 1 };
        _context.Currency.Add(czk);
        _context.SaveChanges();
        _currencyCzkId = czk.Id;

        var issuer = new Client
        {
            RegistrationNumber = "11111111",
            CompanyName = "Issuer",
            IsIssuer = true,
            IsActive = true,
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();
        _issuerId = issuer.Id;

        var bankAccount = new BankAccount
        {
            ClientId = issuer.Id,
            AccountNumber = "1000000000/0100",
            CurrencyCode = "CZK",
            IsDefault = true,
        };
        _context.BankAccount.Add(bankAccount);
        _context.SaveChanges();
        _bankAccountId = bankAccount.Id;
    }

    private RecognizedCounterparty AddEntry(
        string label, string account, string? vs = null, string? ss = null, bool isActive = true)
    {
        var entry = new RecognizedCounterparty
        {
            Label = label,
            CounterpartyAccount = account,
            VariableSymbol = vs,
            SpecificSymbol = ss,
            IsActive = isActive,
        };
        _context.RecognizedCounterparty.Add(entry);
        _context.SaveChanges();
        return entry;
    }

    private Invoice AddInvoice(string? vs, decimal total)
    {
        var invoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = $"INV-{Guid.NewGuid():N}"[..12],
            IssueDate = DateTime.UtcNow.AddDays(-10),
            DueDate = DateTime.UtcNow.AddDays(4),
            IssuerId = _issuerId,
            VariableSymbol = vs,
            TotalBeforeVat = total,
            TotalVat = 0m,
            TotalWithVat = total,
            CurrencyId = _currencyCzkId,
            InvoiceItem = new List<InvoiceItem>(),
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();
        return invoice;
    }

    private ReceivedInvoice AddReceivedInvoice(string? vs, decimal total, string? supplierAccount)
    {
        var supplier = new Client
        {
            RegistrationNumber = $"{Random.Shared.Next(10000000, 99999999)}",
            CompanyName = "Supplier",
            IsIssuer = false,
            IsActive = true,
        };
        _context.Client.Add(supplier);
        _context.SaveChanges();

        var ri = new ReceivedInvoice
        {
            DocumentNumber = $"RI-{Guid.NewGuid():N}"[..12],
            Status = EReceivedInvoiceStatus.Approved,
            SupplierId = supplier.Id,
            CurrencyId = _currencyCzkId,
            IssueDate = DateTime.UtcNow.AddDays(-5),
            DueDate = DateTime.UtcNow.AddDays(5),
            VariableSymbol = vs,
            BankAccountNumber = supplierAccount,
            TotalBeforeVat = total,
            TotalVat = 0m,
            TotalWithVat = total,
        };
        _context.ReceivedInvoice.Add(ri);
        _context.SaveChanges();
        return ri;
    }

    private BankTransaction AddTransaction(
        string? account,
        EPaymentDirection direction,
        string? vs = null,
        decimal amount = 500m)
    {
        var tx = new BankTransaction
        {
            BankAccountId = _bankAccountId,
            DeduplicationHash = Guid.NewGuid().ToString("N"),
            TransactionDate = DateTime.UtcNow,
            Amount = amount,
            CurrencyCode = "CZK",
            Direction = direction,
            VariableSymbol = vs,
            CounterpartyAccount = account,
            ImportSource = EImportSource.Manual,
            MatchStatus = EMatchStatus.Unmatched,
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();
        return tx;
    }
}
