using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Application.Service;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for InvoiceService paged query filtering.
/// Focuses on verifying that deleted invoices are hidden by default
/// but shown when the user explicitly filters by Deleted status.
/// </summary>
public class InvoiceServiceFilterTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly ILogger<InvoiceService> _logger;
    private readonly INumberSequenceService _numberSequence;

    public InvoiceServiceFilterTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();
        _service = new InvoiceService(
            _context,
            _numberSequence,
            Substitute.For<IAdvanceTaxReceiptService>(),
            _logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds a client, issuer, currency, and invoices in various statuses including Deleted.
    /// Each entity is saved separately to satisfy InMemoryDatabase FK resolution.
    /// </summary>
    private void SeedTestData()
    {
        // Save reference entities first — InMemoryDb enforces IsRequired from DbContext config
        // RegistrationNumber is required by DbContext config despite being string? on the entity
        _context.Client.Add(new Client { Id = 1, CompanyName = "Customer A", RegistrationNumber = "REG001", IsIssuer = false, IsActive = true });
        _context.SaveChanges();
        _context.Client.Add(new Client { Id = 2, CompanyName = "My Company", RegistrationNumber = "REG002", IsIssuer = true, IsActive = true });
        _context.SaveChanges();
        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();

        // Add invoices one at a time — InMemoryDb resolves non-nullable Issuer nav property via FK
        AddInvoice("INV-001", EInvoiceStatus.Draft);
        AddInvoice("INV-002", EInvoiceStatus.Completed);
        AddInvoice("INV-003", EInvoiceStatus.Paid);
        AddInvoice("INV-DEL1", EInvoiceStatus.Deleted);
        AddInvoice("INV-DEL2", EInvoiceStatus.Deleted);
    }

    /// <summary>
    /// Helper to add a single invoice with proper FK setup for InMemoryDatabase.
    /// </summary>
    private void AddInvoice(string docNumber, EInvoiceStatus status)
    {
        _context.Invoice.Add(new Invoice
        {
            DocumentNumber = docNumber,
            ClientId = 1, IssuerId = 2, CurrencyId = 1,
            Status = status, DocumentType = EDocumentType.Invoice,
            IssueDate = DateTime.UtcNow,
            InvoiceItem = new List<InvoiceItem>()
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GetInvoicesPagedAsync_ShouldExcludeDeleted_WhenNoStatusFilter()
    {
        // Arrange — no status filter (null) should hide deleted invoices
        var filter = new InvoiceFilterDto { Page = 1, PageSize = 50 };

        // Act
        var result = await _service.GetInvoicesPagedAsync(filter);

        // Assert — 3 non-deleted invoices (Draft, Completed, Paid)
        result.Items.Count.ShouldBe(3);
        result.Items.ShouldNotContain(i => i.Status == EInvoiceStatus.Deleted);
    }

    [Fact]
    public async Task GetInvoicesPagedAsync_ShouldShowDeleted_WhenStatusFilterIsDeleted()
    {
        // Arrange — explicitly filter by Deleted status
        var filter = new InvoiceFilterDto
        {
            Page = 1, PageSize = 50,
            Status = EInvoiceStatus.Deleted
        };

        // Act
        var result = await _service.GetInvoicesPagedAsync(filter);

        // Assert — only the 2 deleted invoices should be returned
        result.Items.Count.ShouldBe(2);
        result.Items.ShouldAllBe(i => i.Status == EInvoiceStatus.Deleted);
    }

    [Fact]
    public async Task GetInvoicesPagedAsync_ShouldShowOnlyDrafts_WhenStatusFilterIsDraft()
    {
        // Arrange — filter by Draft status
        var filter = new InvoiceFilterDto
        {
            Page = 1, PageSize = 50,
            Status = EInvoiceStatus.Draft
        };

        // Act
        var result = await _service.GetInvoicesPagedAsync(filter);

        // Assert — only the 1 draft invoice
        result.Items.Count.ShouldBe(1);
        result.Items.First().DocumentNumber.ShouldBe("INV-001");
    }

    [Fact]
    public async Task GetInvoicesPagedAsync_TotalCount_ShouldExcludeDeleted_ByDefault()
    {
        // Arrange
        var filter = new InvoiceFilterDto { Page = 1, PageSize = 50 };

        // Act
        var result = await _service.GetInvoicesPagedAsync(filter);

        // Assert — TotalCount should be 3 (not 5)
        result.TotalCount.ShouldBe(3);
    }
}
