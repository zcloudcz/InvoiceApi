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
        _sut = new PaymentMatchingService(_context, Substitute.For<ILogger<PaymentMatchingService>>());

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

    // ─── GetPaymentsForInvoiceAsync tests (#31) ──────────────────────────

    [Fact]
    public async Task GetPaymentsForInvoice_DirectMatch_ReturnsPayment()
    {
        // Arrange: payment matched directly to a Proforma.
        var proforma = AddProforma(vs: "2026100", total: 1000m);
        var tx = AddTransaction(vs: "2026100", amount: 1000m);
        AddPaymentMatch(tx, proforma, 1000m);

        // Act: query from the proforma's perspective.
        var payments = await _sut.GetPaymentsForInvoiceAsync(proforma.Id);

        // Assert.
        payments.Count.ShouldBe(1);
        payments[0].MatchedAmount.ShouldBe(1000m);
        payments[0].MatchedInvoiceId.ShouldBe(proforma.Id);
    }

    [Fact]
    public async Task GetPaymentsForInvoice_PaymentOnProforma_VisibleFromTaxReceipt()
    {
        // Arrange: proforma with a direct PaymentMatch; then a DPP linked to it.
        var proforma = AddProforma(vs: "2026101", total: 1000m);
        var dpp = AddTaxReceiptForAdvance(proforma, 1000m);
        var tx = AddTransaction(vs: "2026101", amount: 1000m);
        // Payment is stored on the Proforma (e.g., matched before DPP was issued).
        AddPaymentMatch(tx, proforma, 1000m);

        // Act: query from DPP's perspective — must see the same 1 payment.
        var dppPayments = await _sut.GetPaymentsForInvoiceAsync(dpp.Id);
        var proformaPayments = await _sut.GetPaymentsForInvoiceAsync(proforma.Id);

        // Both views must show the same single payment.
        dppPayments.Count.ShouldBe(1);
        proformaPayments.Count.ShouldBe(1);
        dppPayments[0].Id.ShouldBe(proformaPayments[0].Id);
    }

    [Fact]
    public async Task GetPaymentsForInvoice_PaymentOnTaxReceipt_VisibleFromProforma()
    {
        // Arrange: DPP issued first, payment stored on DPP; proforma is the parent.
        var proforma = AddProforma(vs: "2026102", total: 1000m);
        var dpp = AddTaxReceiptForAdvance(proforma, 1000m);
        var tx = AddTransaction(vs: "2026102", amount: 1000m);
        // Payment stored on the DPP (the canonical new location per #31 AC).
        AddPaymentMatch(tx, dpp, 1000m);

        // Act.
        var proformaPayments = await _sut.GetPaymentsForInvoiceAsync(proforma.Id);
        var dppPayments = await _sut.GetPaymentsForInvoiceAsync(dpp.Id);

        // Proforma panel must see the DPP's payment.
        proformaPayments.Count.ShouldBe(1);
        dppPayments.Count.ShouldBe(1);
        proformaPayments[0].Id.ShouldBe(dppPayments[0].Id);
    }

    [Fact]
    public async Task GetPaymentsForInvoice_TwoDppsOnOneProforma_AllPaymentsVisibleFromEachDocument()
    {
        // Arrange: proforma paid in two instalments → two DPPs, one payment each.
        var proforma = AddProforma(vs: "2026103", total: 1000m);
        var dpp1 = AddTaxReceiptForAdvance(proforma, 600m);
        var dpp2 = AddTaxReceiptForAdvance(proforma, 400m);
        var tx1 = AddTransaction(vs: "2026103", amount: 600m);
        var tx2 = AddTransaction(vs: "2026103", amount: 400m);
        AddPaymentMatch(tx1, dpp1, 600m);
        AddPaymentMatch(tx2, dpp2, 400m);

        // Act.
        var proformaPayments = await _sut.GetPaymentsForInvoiceAsync(proforma.Id);
        var dpp1Payments = await _sut.GetPaymentsForInvoiceAsync(dpp1.Id);
        var dpp2Payments = await _sut.GetPaymentsForInvoiceAsync(dpp2.Id);

        // Proforma sees both payments (total 2).
        proformaPayments.Count.ShouldBe(2);
        proformaPayments.Sum(p => p.MatchedAmount).ShouldBe(1000m);

        // Each DPP also sees both payments (cross-link via proforma).
        dpp1Payments.Count.ShouldBe(2);
        dpp2Payments.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetPaymentsForInvoice_NoOriginalInvoiceId_ReturnsDirectMatchesOnly()
    {
        // Regression: plain Invoice (not Proforma/DPP) with no OriginalInvoiceId
        // must behave exactly as before — only direct PaymentMatch rows returned.
        var invoice = AddInvoice(vs: "2026104", total: 500m);
        var tx = AddTransaction(vs: "2026104", amount: 500m);
        AddPaymentMatch(tx, invoice, 500m);

        var payments = await _sut.GetPaymentsForInvoiceAsync(invoice.Id);

        payments.Count.ShouldBe(1);
        payments[0].MatchedInvoiceId.ShouldBe(invoice.Id);
    }

    [Fact]
    public async Task GetPaymentsForInvoice_UnknownId_ReturnsEmptyList()
    {
        // Querying a non-existent invoice id must return an empty list, not throw.
        var payments = await _sut.GetPaymentsForInvoiceAsync(invoiceId: long.MaxValue);

        payments.ShouldBeEmpty();
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

    /// <summary>Creates a Proforma invoice with the given variable symbol.</summary>
    private Invoice AddProforma(string? vs, decimal total)
    {
        var invoice = new Invoice
        {
            DocumentType = EDocumentType.Proforma,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = $"PRO-{Guid.NewGuid():N}"[..12],
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

    /// <summary>
    /// Creates a TaxReceiptForAdvance (DPP) linked to the given proforma via OriginalInvoiceId.
    /// The DPP inherits the proforma's variable symbol, matching the #5 guarantee.
    /// </summary>
    private Invoice AddTaxReceiptForAdvance(Invoice proforma, decimal total)
    {
        var dpp = new Invoice
        {
            DocumentType = EDocumentType.TaxReceiptForAdvance,
            Status = EInvoiceStatus.Completed,
            DocumentNumber = $"DPP-{Guid.NewGuid():N}"[..12],
            IssueDate = DateTime.UtcNow.AddDays(-5),
            DueDate = DateTime.UtcNow.AddDays(9),
            IssuerId = _issuerId,
            ClientId = _clientId,
            // Inherit VS from proforma — guaranteed by IssueFromPaidProformaAsync (#5).
            VariableSymbol = proforma.VariableSymbol,
            OriginalInvoiceId = proforma.Id,
            TotalBeforeVat = total,
            TotalVat = 0m,
            TotalWithVat = total,
            CurrencyId = _currencyCzkId,
            InvoiceItem = new List<InvoiceItem>(),
        };
        _context.Invoice.Add(dpp);
        _context.SaveChanges();
        return dpp;
    }

    /// <summary>Creates a PaymentMatch row directly linking a transaction to an invoice.</summary>
    private PaymentMatch AddPaymentMatch(BankTransaction tx, Invoice invoice, decimal amount)
    {
        var match = new PaymentMatch
        {
            BankTransactionId = tx.Id,
            InvoiceId = invoice.Id,
            MatchedAmount = amount,
            MatchedBy = EMatchType.Auto,
            MatchedAt = DateTime.UtcNow,
        };
        _context.PaymentMatch.Add(match);
        _context.SaveChanges();
        return match;
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
