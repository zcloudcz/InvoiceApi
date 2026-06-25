// ============================================================================
// AutoMatchServiceTests — unit tests for the auto-match methods added in #122.
//
// Tests for:
//   FindAutoMatchForInvoiceAsync   — VS match, paid-invoice skip, no candidates → null
//   FindAutoMatchForReceivedInvoiceAsync — VS match, Paid skip, no candidates → null
//   ConfirmAutoMatchAsync (invoice)  — creates PaymentMatch, sets Invoice.Paid
//   ConfirmAutoMatchAsync (received) — creates PaymentMatch, sets ReceivedInvoice.Paid
//   GetPaymentsForReceivedInvoiceAsync — returns PaymentMatchDto list
//
// Uses EF Core InMemory database (no PostgreSQL needed).
// ============================================================================

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
/// Tests for <see cref="PaymentMatchingService.FindAutoMatchForInvoiceAsync"/>.
/// </summary>
public class FindAutoMatchForInvoice_Tests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly PaymentMatchingService _service;

    public FindAutoMatchForInvoice_Tests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _service = new PaymentMatchingService(_context, Substitute.For<INotificationService>(), Substitute.For<ILogger<PaymentMatchingService>>());

        SeedReference();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ── Seed helpers ─────────────────────────────────────────────────────────

    private void SeedReference()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Issuer Co", RegistrationNumber = "R001",
            IsIssuer = true, IsActive = true
        });
        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.BankAccount.Add(new BankAccount
        {
            Id = 1, ClientId = 1, AccountNumber = "123456789/0100", IsDefault = true
        });
        _context.SaveChanges();
    }

    private BankTransaction MakeTx(
        EPaymentDirection direction = EPaymentDirection.Incoming,
        EMatchStatus status = EMatchStatus.Unmatched,
        string? vs = null,
        decimal amount = 1000m,
        string currency = "CZK")
    {
        var tx = new BankTransaction
        {
            BankAccountId = 1,
            TransactionDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            Amount = amount,
            CurrencyCode = currency,
            Direction = direction,
            MatchStatus = status,
            VariableSymbol = vs,
            DeduplicationHash = Guid.NewGuid().ToString(),
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();
        return tx;
    }

    private Invoice MakeInvoice(
        EInvoiceStatus status = EInvoiceStatus.Completed,
        string? vs = "123456",
        decimal total = 1000m,
        decimal paid = 0m,
        DateTime? dueDate = null)
    {
        var invoice = new Invoice
        {
            DocumentNumber = "FAK-TEST",
            DocumentType = EDocumentType.Invoice,
            Status = status,
            IssuerId = 1,
            ClientId = 1,
            CurrencyId = 1,
            TotalWithVat = total,
            PaidAmount = paid,
            VariableSymbol = vs,
            DueDate = dueDate,
            IssueDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            TaxableSupplyDate = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalBeforeVat = total,
            TotalVat = 0m,
        };
        _context.Invoice.Add(invoice);
        _context.SaveChanges();
        return invoice;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAutoMatch_Invoice_VsMatch_ReturnsProposal()
    {
        // Arrange — an unmatched incoming transaction with VS = "123456"
        var tx = MakeTx(vs: "123456", amount: 1000m);
        var invoice = MakeInvoice(vs: "123456", total: 1000m);

        // Act
        var result = await _service.FindAutoMatchForInvoiceAsync(invoice.Id);

        // Assert — proposal maps the transaction
        result.ShouldNotBeNull();
        result.BankTransactionId.ShouldBe(tx.Id);
        result.Amount.ShouldBe(1000m);
    }

    [Fact]
    public async Task FindAutoMatch_Invoice_AlreadyPaid_ReturnsNull()
    {
        // Arrange — invoice is already fully paid
        MakeTx(vs: "999999", amount: 1000m);
        var invoice = MakeInvoice(vs: "999999", total: 1000m, status: EInvoiceStatus.Paid);

        // Act — Paid invoice should be skipped without searching
        var result = await _service.FindAutoMatchForInvoiceAsync(invoice.Id);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindAutoMatch_Invoice_NoCandidates_ReturnsNull()
    {
        // Arrange — invoice exists but no unmatched transactions
        var invoice = MakeInvoice(vs: "777777", total: 500m);
        // No BankTransaction added

        // Act
        var result = await _service.FindAutoMatchForInvoiceAsync(invoice.Id);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindAutoMatch_Invoice_IgnoredTransaction_NotMatched()
    {
        // Arrange — transaction is Ignored → must be skipped
        MakeTx(vs: "123456", amount: 1000m, status: EMatchStatus.Ignored);
        var invoice = MakeInvoice(vs: "123456", total: 1000m);

        // Act
        var result = await _service.FindAutoMatchForInvoiceAsync(invoice.Id);

        // Assert — Ignored tx must not be returned
        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindAutoMatch_Invoice_NotFound_ReturnsNull()
    {
        // Act — non-existent invoice
        var result = await _service.FindAutoMatchForInvoiceAsync(99999);

        // Assert
        result.ShouldBeNull();
    }
}

/// <summary>
/// Tests for <see cref="PaymentMatchingService.FindAutoMatchForReceivedInvoiceAsync"/>.
/// </summary>
public class FindAutoMatchForReceivedInvoice_Tests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly PaymentMatchingService _service;

    public FindAutoMatchForReceivedInvoice_Tests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _service = new PaymentMatchingService(_context, Substitute.For<INotificationService>(), Substitute.For<ILogger<PaymentMatchingService>>());

        SeedReference();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedReference()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Supplier Ltd", RegistrationNumber = "R002",
            IsIssuer = false, IsActive = true
        });
        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.BankAccount.Add(new BankAccount
        {
            Id = 1, ClientId = 1, AccountNumber = "111111111/0100", IsDefault = true
        });
        _context.SaveChanges();
    }

    private BankTransaction MakeTx(
        EPaymentDirection direction = EPaymentDirection.Outgoing,
        EMatchStatus status = EMatchStatus.Unmatched,
        string? vs = null,
        decimal amount = 1210m,
        string currency = "CZK")
    {
        var tx = new BankTransaction
        {
            BankAccountId = 1,
            TransactionDate = new DateTime(2026, 4, 5, 0, 0, 0, DateTimeKind.Utc),
            Amount = amount,
            CurrencyCode = currency,
            Direction = direction,
            MatchStatus = status,
            VariableSymbol = vs,
            DeduplicationHash = Guid.NewGuid().ToString(),
        };
        _context.BankTransaction.Add(tx);
        _context.SaveChanges();
        return tx;
    }

    private ReceivedInvoice MakeReceivedInvoice(
        EReceivedInvoiceStatus status = EReceivedInvoiceStatus.Approved,
        string? vs = "111111",
        decimal total = 1210m,
        DateTime? dueDate = null)
    {
        var ri = new ReceivedInvoice
        {
            DocumentNumber = "RI-TEST",
            Status = status,
            SupplierId = 1,
            CurrencyId = 1,
            TotalWithVat = total,
            TotalBeforeVat = total,
            TotalVat = 0m,
            VariableSymbol = vs,
            DueDate = dueDate,
        };
        _context.ReceivedInvoice.Add(ri);
        _context.SaveChanges();
        return ri;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAutoMatch_ReceivedInvoice_VsMatch_ReturnsProposal()
    {
        // Arrange — outgoing tx with VS matching the received invoice
        var tx = MakeTx(vs: "111111", amount: 1210m);
        var ri = MakeReceivedInvoice(vs: "111111", total: 1210m);

        // Act
        var result = await _service.FindAutoMatchForReceivedInvoiceAsync(ri.Id);

        // Assert
        result.ShouldNotBeNull();
        result.BankTransactionId.ShouldBe(tx.Id);
        result.Amount.ShouldBe(1210m);
    }

    [Fact]
    public async Task FindAutoMatch_ReceivedInvoice_AlreadyPaid_ReturnsNull()
    {
        // Arrange — received invoice with Paid status
        MakeTx(vs: "111111", amount: 1210m);
        var ri = MakeReceivedInvoice(vs: "111111", status: EReceivedInvoiceStatus.Paid);

        // Act
        var result = await _service.FindAutoMatchForReceivedInvoiceAsync(ri.Id);

        // Assert — Paid received invoice should be skipped
        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindAutoMatch_ReceivedInvoice_NoCandidates_ReturnsNull()
    {
        // Arrange — no outgoing transactions exist
        var ri = MakeReceivedInvoice(vs: "999", total: 500m);

        // Act
        var result = await _service.FindAutoMatchForReceivedInvoiceAsync(ri.Id);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindAutoMatch_ReceivedInvoice_IncomingTxIgnored()
    {
        // Arrange — only incoming transaction exists (wrong direction)
        MakeTx(direction: EPaymentDirection.Incoming, vs: "111111");
        var ri = MakeReceivedInvoice(vs: "111111");

        // Act — incoming tx must not match an outgoing-only query
        var result = await _service.FindAutoMatchForReceivedInvoiceAsync(ri.Id);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task FindAutoMatch_ReceivedInvoice_NotFound_ReturnsNull()
    {
        // Act
        var result = await _service.FindAutoMatchForReceivedInvoiceAsync(88888);

        // Assert
        result.ShouldBeNull();
    }
}

/// <summary>
/// Tests for <see cref="PaymentMatchingService.ConfirmAutoMatchAsync"/> — invoice branch.
/// </summary>
public class ConfirmAutoMatch_Invoice_Tests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly PaymentMatchingService _service;

    public ConfirmAutoMatch_Invoice_Tests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _service = new PaymentMatchingService(_context, Substitute.For<INotificationService>(), Substitute.For<ILogger<PaymentMatchingService>>());

        SeedReference();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedReference()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Issuer", RegistrationNumber = "R1",
            IsIssuer = true, IsActive = true
        });
        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.BankAccount.Add(new BankAccount
        {
            Id = 1, ClientId = 1, AccountNumber = "222/0100", IsDefault = true
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task ConfirmAutoMatch_Invoice_FullPayment_CreatesMatchAndSetsPaid()
    {
        // Arrange — invoice for 1000 CZK, incoming tx for 1000 CZK
        var invoice = new Invoice
        {
            DocumentNumber = "FAK001",
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            IssuerId = 1, ClientId = 1, CurrencyId = 1,
            TotalWithVat = 1000m, PaidAmount = 0m,
            IssueDate = DateTime.UtcNow,
            TaxableSupplyDate = DateTime.UtcNow,
            TotalBeforeVat = 1000m, TotalVat = 0m,
        };
        _context.Invoice.Add(invoice);

        var tx = new BankTransaction
        {
            BankAccountId = 1, Amount = 1000m, CurrencyCode = "CZK",
            Direction = EPaymentDirection.Incoming, MatchStatus = EMatchStatus.Unmatched,
            TransactionDate = DateTime.UtcNow,
            DeduplicationHash = Guid.NewGuid().ToString(),
        };
        _context.BankTransaction.Add(tx);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ConfirmAutoMatchAsync(tx.Id, invoice.Id, null, userId: null);

        // Assert — result contains correct amounts
        result.PaidAmount.ShouldBe(1000m);
        result.Remaining.ShouldBe(0m);

        // PaymentMatch row created
        var match = await _context.PaymentMatch.SingleAsync();
        match.InvoiceId.ShouldBe(invoice.Id);
        match.MatchedAmount.ShouldBe(1000m);
        match.MatchedBy.ShouldBe(EMatchType.Auto);

        // Invoice status updated to Paid
        var updatedInvoice = await _context.Invoice.FindAsync(invoice.Id);
        updatedInvoice!.Status.ShouldBe(EInvoiceStatus.Paid);
        updatedInvoice.PaidAmount.ShouldBe(1000m);
    }

    [Fact]
    public async Task ConfirmAutoMatch_Invoice_BothNull_Throws()
    {
        // Arrange — minimal tx (won't be reached but needed)
        var tx = new BankTransaction
        {
            BankAccountId = 1, Amount = 100m, CurrencyCode = "CZK",
            Direction = EPaymentDirection.Incoming, MatchStatus = EMatchStatus.Unmatched,
            TransactionDate = DateTime.UtcNow,
            DeduplicationHash = Guid.NewGuid().ToString(),
        };
        _context.BankTransaction.Add(tx);
        await _context.SaveChangesAsync();

        // Act + Assert — must throw if neither id is provided
        await Should.ThrowAsync<ArgumentException>(
            () => _service.ConfirmAutoMatchAsync(tx.Id, null, null, null));
    }
}

/// <summary>
/// Tests for <see cref="PaymentMatchingService.ConfirmAutoMatchAsync"/> — received invoice branch.
/// </summary>
public class ConfirmAutoMatch_ReceivedInvoice_Tests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly PaymentMatchingService _service;

    public ConfirmAutoMatch_ReceivedInvoice_Tests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _service = new PaymentMatchingService(_context, Substitute.For<INotificationService>(), Substitute.For<ILogger<PaymentMatchingService>>());

        SeedReference();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedReference()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Supplier", RegistrationNumber = "R2",
            IsIssuer = false, IsActive = true
        });
        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.BankAccount.Add(new BankAccount
        {
            Id = 1, ClientId = 1, AccountNumber = "333/0100", IsDefault = true
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task ConfirmAutoMatch_ReceivedInvoice_FullPayment_SetsStatusPaid()
    {
        // Arrange
        var ri = new ReceivedInvoice
        {
            DocumentNumber = "RI001",
            Status = EReceivedInvoiceStatus.Approved,
            SupplierId = 1, CurrencyId = 1,
            TotalWithVat = 1210m, TotalBeforeVat = 1000m, TotalVat = 210m,
        };
        _context.ReceivedInvoice.Add(ri);

        var tx = new BankTransaction
        {
            BankAccountId = 1, Amount = 1210m, CurrencyCode = "CZK",
            Direction = EPaymentDirection.Outgoing, MatchStatus = EMatchStatus.Unmatched,
            TransactionDate = DateTime.UtcNow,
            DeduplicationHash = Guid.NewGuid().ToString(),
        };
        _context.BankTransaction.Add(tx);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.ConfirmAutoMatchAsync(tx.Id, null, ri.Id, userId: null);

        // Assert — amounts correct
        result.PaidAmount.ShouldBe(1210m);
        result.Remaining.ShouldBe(0m);

        // PaymentMatch links to ReceivedInvoice
        var match = await _context.PaymentMatch.SingleAsync();
        match.ReceivedInvoiceId.ShouldBe(ri.Id);
        match.InvoiceId.ShouldBeNull();
        match.MatchedAmount.ShouldBe(1210m);
        match.MatchedBy.ShouldBe(EMatchType.Auto);

        // ReceivedInvoice status → Paid
        var updated = await _context.ReceivedInvoice.FindAsync(ri.Id);
        updated!.Status.ShouldBe(EReceivedInvoiceStatus.Paid);
    }
}

/// <summary>
/// Tests for <see cref="PaymentMatchingService.GetPaymentsForReceivedInvoiceAsync"/>.
/// </summary>
public class GetPaymentsForReceivedInvoice_Tests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly PaymentMatchingService _service;

    public GetPaymentsForReceivedInvoice_Tests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _service = new PaymentMatchingService(_context, Substitute.For<INotificationService>(), Substitute.For<ILogger<PaymentMatchingService>>());

        SeedReference();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    private void SeedReference()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Supplier", RegistrationNumber = "R3",
            IsIssuer = false, IsActive = true
        });
        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.BankAccount.Add(new BankAccount
        {
            Id = 1, ClientId = 1, AccountNumber = "444/0100", IsDefault = true
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetPayments_ReceivedInvoice_ReturnsMatchList()
    {
        // Arrange — received invoice with one PaymentMatch
        var ri = new ReceivedInvoice
        {
            DocumentNumber = "RI-PAY001",
            Status = EReceivedInvoiceStatus.Paid,
            SupplierId = 1, CurrencyId = 1,
            TotalWithVat = 500m, TotalBeforeVat = 500m, TotalVat = 0m,
        };
        _context.ReceivedInvoice.Add(ri);

        var tx = new BankTransaction
        {
            BankAccountId = 1, Amount = 500m, CurrencyCode = "CZK",
            Direction = EPaymentDirection.Outgoing, MatchStatus = EMatchStatus.Matched,
            TransactionDate = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            DeduplicationHash = Guid.NewGuid().ToString(),
        };
        _context.BankTransaction.Add(tx);
        await _context.SaveChangesAsync();

        var match = new PaymentMatch
        {
            BankTransactionId = tx.Id,
            ReceivedInvoiceId = ri.Id,
            MatchedAmount = 500m,
            MatchedBy = EMatchType.Auto,
            MatchedAt = DateTime.UtcNow,
        };
        _context.PaymentMatch.Add(match);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPaymentsForReceivedInvoiceAsync(ri.Id);

        // Assert
        result.ShouldNotBeNull();
        result.Count.ShouldBe(1);
        result[0].MatchedAmount.ShouldBe(500m);
        result[0].MatchedBy.ShouldBe(EMatchType.Auto);
        result[0].BankTransactionId.ShouldBe(tx.Id);
    }

    [Fact]
    public async Task GetPayments_ReceivedInvoice_NoMatches_ReturnsEmpty()
    {
        // Arrange — received invoice with no PaymentMatch rows
        var ri = new ReceivedInvoice
        {
            DocumentNumber = "RI-EMPTY",
            Status = EReceivedInvoiceStatus.Approved,
            SupplierId = 1, CurrencyId = 1,
            TotalWithVat = 300m, TotalBeforeVat = 300m, TotalVat = 0m,
        };
        _context.ReceivedInvoice.Add(ri);
        await _context.SaveChangesAsync();

        // Act
        var result = await _service.GetPaymentsForReceivedInvoiceAsync(ri.Id);

        // Assert
        result.ShouldNotBeNull();
        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task GetPayments_ReceivedInvoice_NotFound_ReturnsEmpty()
    {
        // Act — non-existent received invoice
        var result = await _service.GetPaymentsForReceivedInvoiceAsync(77777);

        // Assert — returns empty list (not null, not throws)
        result.ShouldNotBeNull();
        result.Count.ShouldBe(0);
    }
}
