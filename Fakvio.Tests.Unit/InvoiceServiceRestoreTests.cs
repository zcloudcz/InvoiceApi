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
/// Unit tests for InvoiceService.RestoreInvoiceAsync.
/// Covers restoring deleted invoices, rejecting non-deleted invoices,
/// and handling non-existent invoice IDs.
/// </summary>
public class InvoiceServiceRestoreTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly ILogger<InvoiceService> _logger;
    private readonly INumberSequenceService _numberSequence;

    public InvoiceServiceRestoreTests()
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

        _service = new InvoiceService(_context, _numberSequence, Substitute.For<ITenantReadinessService>(), _logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds required reference entities — customer, issuer, currency.
    /// Also seeds invoices in various statuses for testing restore logic.
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

        // Deleted invoice — this one should be restorable.
        _context.Invoice.Add(new Invoice
        {
            Id = 100,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Deleted,
            IssuerId = 2,
            ClientId = 1,
            CurrencyId = 1,
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();

        // Draft invoice — should NOT be restorable (not deleted).
        _context.Invoice.Add(new Invoice
        {
            Id = 101,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Draft,
            IssuerId = 2,
            ClientId = 1,
            CurrencyId = 1,
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();

        // Completed invoice — should NOT be restorable (not deleted).
        _context.Invoice.Add(new Invoice
        {
            Id = 102,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            IssuerId = 2,
            ClientId = 1,
            CurrencyId = 1,
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();

        // Paid invoice — should NOT be restorable (not deleted).
        _context.Invoice.Add(new Invoice
        {
            Id = 103,
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Paid,
            IssuerId = 2,
            ClientId = 1,
            CurrencyId = 1,
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();
    }

    // ─── Restore tests ──────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreInvoiceAsync_DeletedInvoice_RestoresToDraft()
    {
        // Act — restore the deleted invoice (ID=100)
        var result = await _service.RestoreInvoiceAsync(100);

        // Assert — status should now be Draft
        result.ShouldNotBeNull();
        result.Id.ShouldBe(100);
        result.Status.ShouldBe(EInvoiceStatus.Draft);

        // Verify the database was actually updated
        var dbInvoice = await _context.Invoice.FindAsync(100L);
        dbInvoice!.Status.ShouldBe(EInvoiceStatus.Draft);
    }

    [Fact]
    public async Task RestoreInvoiceAsync_NonExistentId_ReturnsNull()
    {
        // Act — try to restore an invoice that doesn't exist
        var result = await _service.RestoreInvoiceAsync(999);

        // Assert — null means "not found"
        result.ShouldBeNull();
    }

    [Fact]
    public async Task RestoreInvoiceAsync_DraftInvoice_ThrowsInvalidOperation()
    {
        // Act & Assert — Draft invoices cannot be "restored" (they aren't deleted)
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.RestoreInvoiceAsync(101));

        ex.Message.ShouldContain("Only deleted invoices can be restored");
    }

    [Fact]
    public async Task RestoreInvoiceAsync_CompletedInvoice_ThrowsInvalidOperation()
    {
        // Act & Assert — Completed invoices cannot be restored
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.RestoreInvoiceAsync(102));

        ex.Message.ShouldContain("Only deleted invoices can be restored");
    }

    [Fact]
    public async Task RestoreInvoiceAsync_PaidInvoice_ThrowsInvalidOperation()
    {
        // Act & Assert — Paid invoices cannot be restored
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.RestoreInvoiceAsync(103));

        ex.Message.ShouldContain("Only deleted invoices can be restored");
    }

    // ─── GetById now returns deleted invoices ────────────────────────────────

    [Fact]
    public async Task GetInvoiceByIdAsync_DeletedInvoice_ReturnsInvoice()
    {
        // Act — GetInvoiceByIdAsync should now return deleted invoices
        var result = await _service.GetInvoiceByIdAsync(100);

        // Assert — previously this returned null; now it returns the deleted invoice
        result.ShouldNotBeNull();
        result.Id.ShouldBe(100);
        result.Status.ShouldBe(EInvoiceStatus.Deleted);
    }
}
