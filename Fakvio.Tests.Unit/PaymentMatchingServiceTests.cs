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
/// Core behaviour of <see cref="PaymentMatchingService"/> — the auto-matcher.
/// We use the in-memory provider to give the service a real DbContext because the
/// matcher runs non-trivial queries (Include chains, aggregates) that are worth
/// exercising against an actual EF Core pipeline.
/// </summary>
public class PaymentMatchingServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly PaymentMatchingService _sut;

    // Seed ids used throughout the tests.
    private long _issuerId;
    private long _clientId;
    private long _currencyCzkId;
    private long _bankAccountId;

    public PaymentMatchingServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new TenantDbContext(options);
        // IAdvanceTaxReceiptService is stubbed here — PaymentMatchingServiceTests cover
        // only the matching algorithm, not the DPP creation. DPP integration is covered
        // by dedicated AdvanceTaxReceiptServiceTests and InvoiceMarkPaidDppTests.
        _sut = new PaymentMatchingService(
            _context,
            Substitute.For<IAdvanceTaxReceiptService>(),
            Substitute.For<ILogger<PaymentMatchingService>>());

        Seed();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // ─── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExactVsAndAmount_MarksInvoicePaid()
    {
        var invoice = AddInvoice(vs: "2026001", total: 1000m);
        var tx = AddTransaction(vs: "2026001", amount: 1000m);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);

        reloaded.Status.ShouldBe(EInvoiceStatus.Paid);
        reloaded.PaidAmount.ShouldBe(1000m);
        reloaded.PaidAt.ShouldNotBeNull();
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.Matched);
    }

    [Fact]
    public async Task PartialPayment_SetsPartiallyPaid()
    {
        var invoice = AddInvoice(vs: "2026002", total: 1000m);
        var tx = AddTransaction(vs: "2026002", amount: 400m);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        reloaded.Status.ShouldBe(EInvoiceStatus.PartiallyPaid);
        reloaded.PaidAmount.ShouldBe(400m);
        reloaded.PaidAt.ShouldBeNull();
    }

    [Fact]
    public async Task Overpayment_PaysFullInvoice_AndMarksTransactionPartiallyMatched()
    {
        var invoice = AddInvoice(vs: "2026003", total: 1000m);
        var tx = AddTransaction(vs: "2026003", amount: 1500m);

        await _sut.MatchAsync(tx.Id);

        var reloaded = await _context.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);

        reloaded.Status.ShouldBe(EInvoiceStatus.Paid);
        reloaded.PaidAmount.ShouldBe(1000m);
        // Transaction still has 500 unallocated → PartiallyMatched.
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.PartiallyMatched);
    }

    [Fact]
    public async Task MultipleInvoicesSameVs_AmountMatchesOneExactly_ChoosesThatOne()
    {
        var small = AddInvoice(vs: "777", total: 500m);
        var large = AddInvoice(vs: "777", total: 1500m);
        var tx = AddTransaction(vs: "777", amount: 500m);

        await _sut.MatchAsync(tx.Id);

        var smallRe = await _context.Invoice.AsNoTracking().FirstAsync(i => i.Id == small.Id);
        var largeRe = await _context.Invoice.AsNoTracking().FirstAsync(i => i.Id == large.Id);

        smallRe.Status.ShouldBe(EInvoiceStatus.Paid);
        largeRe.Status.ShouldBe(EInvoiceStatus.Completed);
    }

    [Fact]
    public async Task MultipleInvoicesSameVs_NoExactAmount_NeedsReview()
    {
        AddInvoice(vs: "888", total: 500m);
        AddInvoice(vs: "888", total: 1500m);
        var tx = AddTransaction(vs: "888", amount: 999m);

        await _sut.MatchAsync(tx.Id);

        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.NeedsReview);

        // No invoice should have been touched.
        var all = await _context.Invoice.AsNoTracking().ToListAsync();
        all.ShouldAllBe(i => i.Status == EInvoiceStatus.Completed);
    }

    [Fact]
    public async Task NoVs_NoAccount_LeavesUnmatched()
    {
        AddInvoice(vs: "111", total: 1000m);
        var tx = AddTransaction(vs: null, amount: 1000m);

        await _sut.MatchAsync(tx.Id);

        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    [Fact]
    public async Task AlreadyPaidInvoice_IsSkipped()
    {
        var invoice = AddInvoice(vs: "222", total: 1000m);
        invoice.Status = EInvoiceStatus.Paid;
        invoice.PaidAmount = 1000m;
        invoice.PaidAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var tx = AddTransaction(vs: "222", amount: 1000m);

        await _sut.MatchAsync(tx.Id);

        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    [Fact]
    public async Task CurrencyMismatch_LeavesUnmatched()
    {
        var eur = new Currency { Code = "EUR", Name = "Euro", Symbol = "€", IsActive = true, SortOrder = 99 };
        _context.Currency.Add(eur);
        await _context.SaveChangesAsync();

        // Invoice in CZK, transaction in EUR.
        AddInvoice(vs: "333", total: 1000m);
        var tx = AddTransaction(vs: "333", amount: 1000m, currency: "EUR");

        await _sut.MatchAsync(tx.Id);

        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    [Fact]
    public async Task RunningMatchTwice_IsIdempotent()
    {
        var invoice = AddInvoice(vs: "444", total: 1000m);
        var tx = AddTransaction(vs: "444", amount: 1000m);

        await _sut.MatchAsync(tx.Id);
        await _sut.MatchAsync(tx.Id);

        var matchCount = await _context.PaymentMatch.CountAsync(m => m.InvoiceId == invoice.Id);
        matchCount.ShouldBe(1);
    }

    [Fact]
    public async Task ManualMatch_CreatesMatchAndUpdatesAmounts()
    {
        var invoice = AddInvoice(vs: "555", total: 1000m);
        var tx = AddTransaction(vs: null, amount: 600m);

        var result = await _sut.ManualMatchAsync(tx.Id, invoice.Id, 600m, note: "test", userId: 42);

        result.InvoicePaidAmount.ShouldBe(600m);
        result.InvoiceRemaining.ShouldBe(400m);

        var reloaded = await _context.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        reloaded.Status.ShouldBe(EInvoiceStatus.PartiallyPaid);
    }

    [Fact]
    public async Task ManualMatch_OverAmount_Throws()
    {
        var invoice = AddInvoice(vs: "666", total: 1000m);
        var tx = AddTransaction(vs: null, amount: 500m);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.ManualMatchAsync(tx.Id, invoice.Id, 600m, null, null));
    }

    [Fact]
    public async Task Unmatch_RestoresAmounts()
    {
        var invoice = AddInvoice(vs: "aaa", total: 1000m);
        var tx = AddTransaction(vs: "aaa", amount: 1000m);
        await _sut.MatchAsync(tx.Id);

        var match = await _context.PaymentMatch.FirstAsync(m => m.InvoiceId == invoice.Id);

        await _sut.UnmatchAsync(match.Id, reason: "test", userId: 1);

        var reloaded = await _context.Invoice.AsNoTracking().FirstAsync(i => i.Id == invoice.Id);
        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.PaidAmount.ShouldBe(0m);
        reloaded.Status.ShouldBe(EInvoiceStatus.Completed);
        reloaded.PaidAt.ShouldBeNull();
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    [Fact]
    public async Task Ignore_SetsIgnoredStatus()
    {
        var tx = AddTransaction(vs: "bbb", amount: 100m);

        await _sut.IgnoreAsync(tx.Id, userId: 42);

        var reloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        reloaded.MatchStatus.ShouldBe(EMatchStatus.Ignored);
    }

    [Fact]
    public async Task DraftInvoice_IsNotMatched()
    {
        var invoice = AddInvoice(vs: "ccc", total: 1000m);
        invoice.Status = EInvoiceStatus.Draft;
        await _context.SaveChangesAsync();

        var tx = AddTransaction(vs: "ccc", amount: 1000m);
        await _sut.MatchAsync(tx.Id);

        var txReloaded = await _context.BankTransaction.AsNoTracking().FirstAsync(t => t.Id == tx.Id);
        txReloaded.MatchStatus.ShouldBe(EMatchStatus.Unmatched);
    }

    // ─── Seeding helpers ─────────────────────────────────────────────────

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
        var client = new Client
        {
            RegistrationNumber = "22222222",
            CompanyName = "Client",
            IsIssuer = false,
            IsActive = true,
        };
        _context.Client.Add(issuer);
        _context.Client.Add(client);
        _context.SaveChanges();
        _issuerId = issuer.Id;
        _clientId = client.Id;

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
            ClientId = _clientId,
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

    private BankTransaction AddTransaction(string? vs, decimal amount, string currency = "CZK")
    {
        var tx = new BankTransaction
        {
            BankAccountId = _bankAccountId,
            DeduplicationHash = Guid.NewGuid().ToString("N"),
            TransactionDate = DateTime.UtcNow,
            Amount = amount,
            CurrencyCode = currency,
            Direction = EPaymentDirection.Incoming,
            VariableSymbol = vs,
            ImportSource = EImportSource.Manual,
            MatchStatus = EMatchStatus.Unmatched,
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();
        return tx;
    }
}
