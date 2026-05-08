using Fakvio.Contracts.Dto.Invoice;
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
/// Unit tests for InvoiceService.MarkAsUnpaidAsync.
/// Covers reverting a Paid invoice to Completed, rejecting non-Paid invoices,
/// resetting PaidAt and PaidAmount, and unlinking PaymentMatch records.
/// </summary>
public class InvoiceServiceMarkAsUnpaidTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly ILogger<InvoiceService> _logger;
    private readonly INumberSequenceService _numberSequence;

    public InvoiceServiceMarkAsUnpaidTests()
    {
        // Each test gets its own in-memory database to avoid cross-test interference.
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();

        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV-2026-001");

        _service = new InvoiceService(_context, _numberSequence, _logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds reference entities and invoices in various states for testing MarkAsUnpaid logic.
    /// </summary>
    private void SeedTestData()
    {
        _context.Client.Add(new Client
        {
            Id = 1, CompanyName = "Customer A", RegistrationNumber = "REG001",
            IsIssuer = false, IsActive = true
        });
        _context.SaveChanges();

        _context.Client.Add(new Client
        {
            Id = 2, CompanyName = "My Company", RegistrationNumber = "REG002",
            IsIssuer = true, IsActive = true, IsVatPayer = false
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kc",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();

        // ─── Invoice 100: Paid invoice — the primary test subject ────────────
        _context.Invoice.Add(new Invoice
        {
            Id = 100,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Paid,
            IssuerId = 2,
            ClientId = 1,
            CurrencyId = 1,
            DocumentNumber = "INV-2026-001",
            PaidAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            PaidAmount = 1000m,
            TotalWithVat = 1000m,
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();

        // ─── Invoice 101: Completed invoice — NOT paid, should not be markable ─
        _context.Invoice.Add(new Invoice
        {
            Id = 101,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            IssuerId = 2,
            ClientId = 1,
            CurrencyId = 1,
            DocumentNumber = "INV-2026-002",
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();

        // ─── Invoice 102: Draft invoice — should not be markable as unpaid ───
        _context.Invoice.Add(new Invoice
        {
            Id = 102,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Draft,
            IssuerId = 2,
            ClientId = 1,
            CurrencyId = 1,
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();

        // ─── BankTransaction used for PaymentMatch ────────────────────────────
        // In-memory DB does not enforce FK constraints, so we can insert directly.
        _context.BankAccount.Add(new BankAccount
        {
            Id = 1, AccountNumber = "123456/0100", ClientId = 2
        });
        _context.SaveChanges();

        _context.BankTransaction.Add(new BankTransaction
        {
            Id = 1,
            BankAccountId = 1,
            Amount = 1000m,
            TransactionDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            VariableSymbol = "20261001",
            MatchStatus = EMatchStatus.Matched,
            // Required fields with default/test values:
            DeduplicationHash = "testhash001",
            Direction = EPaymentDirection.Incoming,
            ImportSource = EImportSource.Manual
        });
        _context.SaveChanges();

        // PaymentMatch linking BankTransaction 1 → Invoice 100
        _context.PaymentMatch.Add(new PaymentMatch
        {
            Id = 1,
            BankTransactionId = 1,
            InvoiceId = 100,
            MatchedAmount = 1000m,
            MatchedBy = EMatchType.Manual,
            MatchedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)
        });
        _context.SaveChanges();
    }

    // ─── MarkAsUnpaidAsync — happy-path tests ─────────────────────────────────

    [Fact]
    public async Task MarkAsUnpaidAsync_PaidInvoice_ShouldRevertToCompleted()
    {
        // Act — revert the paid invoice
        var result = await _service.MarkAsUnpaidAsync(100);

        // Assert — status must be Completed
        result.ShouldNotBeNull();
        result.Id.ShouldBe(100);
        result.Status.ShouldBe(EInvoiceStatus.Completed);

        // Verify the database row was actually updated.
        var dbInvoice = await _context.Invoice.FindAsync(100L);
        dbInvoice!.Status.ShouldBe(EInvoiceStatus.Completed);
    }

    [Fact]
    public async Task MarkAsUnpaidAsync_ShouldResetPaidAtAndPaidAmount()
    {
        // Act
        var result = await _service.MarkAsUnpaidAsync(100);

        // Assert — PaidAt must be null, PaidAmount must be zero
        result.PaidAt.ShouldBeNull();
        result.PaidAmount.ShouldBe(0m);

        var dbInvoice = await _context.Invoice.FindAsync(100L);
        dbInvoice!.PaidAt.ShouldBeNull();
        dbInvoice.PaidAmount.ShouldBe(0m);
    }

    [Fact]
    public async Task MarkAsUnpaidAsync_ShouldDeleteLinkedPaymentMatches()
    {
        // Verify we have exactly 1 PaymentMatch for invoice 100 before the call.
        var matchesBefore = await _context.PaymentMatch.Where(m => m.InvoiceId == 100).CountAsync();
        matchesBefore.ShouldBe(1);

        // Act
        await _service.MarkAsUnpaidAsync(100);

        // Assert — all PaymentMatch rows for this invoice must be removed.
        var matchesAfter = await _context.PaymentMatch.Where(m => m.InvoiceId == 100).CountAsync();
        matchesAfter.ShouldBe(0);
    }

    // ─── MarkAsUnpaidAsync — negative tests ──────────────────────────────────

    [Fact]
    public async Task MarkAsUnpaidAsync_NotPaidInvoice_ShouldThrow()
    {
        // Act & Assert — Completed invoice cannot be marked as unpaid.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.MarkAsUnpaidAsync(101));

        ex.Message.ShouldContain("Only paid invoices can be marked as unpaid");
    }

    [Fact]
    public async Task MarkAsUnpaidAsync_DraftInvoice_ShouldThrow()
    {
        // Act & Assert — Draft invoice cannot be marked as unpaid.
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.MarkAsUnpaidAsync(102));

        ex.Message.ShouldContain("Only paid invoices can be marked as unpaid");
    }

    [Fact]
    public async Task MarkAsUnpaidAsync_NonExistentId_ShouldThrowKeyNotFound()
    {
        // Act & Assert — non-existent invoice throws KeyNotFoundException.
        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => _service.MarkAsUnpaidAsync(999));

        ex.Message.ShouldContain("999");
    }
}
