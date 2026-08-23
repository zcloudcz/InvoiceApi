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
/// Unit tests for the proforma cross-link query methods added in issue #33:
///   - GetFinalInvoicesForProformaAsync: returns only Invoice documents linked via OriginalInvoiceId
///   - GetTaxReceiptsForProformaAsync:   returns only TaxReceiptForAdvance documents linked via OriginalInvoiceId
///
/// Both methods must exclude Deleted documents and only return documents that reference
/// the given proforma ID through the OriginalInvoiceId column.
/// </summary>
public class ProformaCrossLinkTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;

    // IDs used across all tests — stable so each test can reference them by name
    private const long ProformaId = 10;
    private const long OtherProformaId = 11;
    private const long FinalInvoice1Id = 20;
    private const long FinalInvoice2Id = 21;
    private const long DeletedFinalId = 22;
    private const long TaxReceiptId = 30;
    private const long DeletedTaxReceiptId = 31;
    private const long UnrelatedInvoiceId = 40;

    public ProformaCrossLinkTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        var logger = Substitute.For<ILogger<InvoiceService>>();
        var numberSequence = Substitute.For<INumberSequenceService>();
        _service = new InvoiceService(_context, numberSequence, Substitute.For<ITenantReadinessService>(), logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Seed helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the minimum set of reference data (Client, Currency) and a set of
    /// invoices that cover all cross-link scenarios:
    /// proforma, final invoices (including deleted), tax receipts (including deleted),
    /// an unrelated invoice, and a second proforma (to verify isolation).
    /// </summary>
    private void SeedTestData()
    {
        // Reference entities — InMemoryDb enforces required fields from DbContext config
        _context.Client.Add(new Client { Id = 1, CompanyName = "Customer", RegistrationNumber = "R1", IsIssuer = false, IsActive = true });
        _context.SaveChanges();
        _context.Client.Add(new Client { Id = 2, CompanyName = "Issuer", RegistrationNumber = "R2", IsIssuer = true, IsActive = true });
        _context.SaveChanges();
        _context.Currency.Add(new Currency { Id = 1, Code = "CZK", Name = "Koruna", Symbol = "Kč", IsActive = true });
        _context.SaveChanges();

        // The proforma being tested
        AddInvoice(ProformaId, EDocumentType.Proforma, EInvoiceStatus.Paid, originalId: null);

        // A second, unrelated proforma — its linked documents must NOT appear in results for ProformaId
        AddInvoice(OtherProformaId, EDocumentType.Proforma, EInvoiceStatus.Completed, originalId: null);

        // Final invoices (DocumentType=Invoice) referencing ProformaId
        AddInvoice(FinalInvoice1Id, EDocumentType.Invoice, EInvoiceStatus.Completed, originalId: ProformaId);
        AddInvoice(FinalInvoice2Id, EDocumentType.Invoice, EInvoiceStatus.Paid, originalId: ProformaId);

        // A deleted final invoice — should be excluded from results
        AddInvoice(DeletedFinalId, EDocumentType.Invoice, EInvoiceStatus.Deleted, originalId: ProformaId);

        // Tax receipt (DPP) referencing ProformaId
        AddInvoice(TaxReceiptId, EDocumentType.TaxReceiptForAdvance, EInvoiceStatus.Completed, originalId: ProformaId);

        // A deleted tax receipt — should be excluded
        AddInvoice(DeletedTaxReceiptId, EDocumentType.TaxReceiptForAdvance, EInvoiceStatus.Deleted, originalId: ProformaId);

        // A final invoice referencing OtherProformaId — must NOT appear in results for ProformaId
        AddInvoice(UnrelatedInvoiceId, EDocumentType.Invoice, EInvoiceStatus.Completed, originalId: OtherProformaId);

        _context.SaveChanges();
    }

    private void AddInvoice(long id, EDocumentType docType, EInvoiceStatus status, long? originalId)
    {
        _context.Invoice.Add(new Invoice
        {
            Id = id,
            DocumentType = docType,
            Status = status,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            OriginalInvoiceId = originalId,
            DocumentNumber = $"DOC-{id}",
            CreatedAt = DateTime.UtcNow
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GetFinalInvoicesForProformaAsync tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFinalInvoicesForProforma_ReturnsOnlyActiveInvoicesForGivenProforma()
    {
        // Act
        var result = await _service.GetFinalInvoicesForProformaAsync(ProformaId);

        // Assert: only the two non-deleted final invoices referencing ProformaId
        result.Count.ShouldBe(2);
        result.Select(r => r.Id).ShouldContain(FinalInvoice1Id);
        result.Select(r => r.Id).ShouldContain(FinalInvoice2Id);
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_ExcludesDeletedFinalInvoices()
    {
        var result = await _service.GetFinalInvoicesForProformaAsync(ProformaId);

        // Deleted final invoice must NOT appear
        result.Select(r => r.Id).ShouldNotContain(DeletedFinalId);
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_ExcludesTaxReceiptsForSameProforma()
    {
        var result = await _service.GetFinalInvoicesForProformaAsync(ProformaId);

        // TaxReceiptForAdvance linked to the same proforma must NOT appear
        result.Select(r => r.Id).ShouldNotContain(TaxReceiptId);
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_DoesNotReturnFinalInvoicesOfOtherProforma()
    {
        var result = await _service.GetFinalInvoicesForProformaAsync(ProformaId);

        // FinalInvoice referencing OtherProformaId must NOT appear
        result.Select(r => r.Id).ShouldNotContain(UnrelatedInvoiceId);
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_ReturnsEmptyList_WhenNoFinalInvoicesExist()
    {
        // Act: query against a proforma that has no linked documents at all
        var result = await _service.GetFinalInvoicesForProformaAsync(OtherProformaId);

        // OtherProformaId only has an Invoice via UnrelatedInvoiceId,
        // so there IS one result — assert it is indeed from OtherProformaId
        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(UnrelatedInvoiceId);
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_ReturnsEmptyList_ForNonExistentProforma()
    {
        var result = await _service.GetFinalInvoicesForProformaAsync(proformaId: 9999);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetFinalInvoicesForProforma_AllResultsHaveDocumentTypeInvoice()
    {
        var result = await _service.GetFinalInvoicesForProformaAsync(ProformaId);

        // Every returned item must be a standard Invoice, not a Proforma or CreditNote
        result.ShouldAllBe(dto => dto.DocumentType == EDocumentType.Invoice);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GetTaxReceiptsForProformaAsync tests
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTaxReceiptsForProforma_ReturnsOnlyActiveTaxReceiptForGivenProforma()
    {
        var result = await _service.GetTaxReceiptsForProformaAsync(ProformaId);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(TaxReceiptId);
    }

    [Fact]
    public async Task GetTaxReceiptsForProforma_ExcludesDeletedTaxReceipts()
    {
        var result = await _service.GetTaxReceiptsForProformaAsync(ProformaId);

        result.Select(r => r.Id).ShouldNotContain(DeletedTaxReceiptId);
    }

    [Fact]
    public async Task GetTaxReceiptsForProforma_ExcludesFinalInvoicesForSameProforma()
    {
        var result = await _service.GetTaxReceiptsForProformaAsync(ProformaId);

        // Standard Invoice documents must NOT appear — only TaxReceiptForAdvance
        result.Select(r => r.Id).ShouldNotContain(FinalInvoice1Id);
        result.Select(r => r.Id).ShouldNotContain(FinalInvoice2Id);
    }

    [Fact]
    public async Task GetTaxReceiptsForProforma_ReturnsEmptyList_ForOtherProforma()
    {
        // OtherProformaId has no tax receipts linked — must return empty
        var result = await _service.GetTaxReceiptsForProformaAsync(OtherProformaId);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetTaxReceiptsForProforma_AllResultsHaveDocumentTypeTaxReceiptForAdvance()
    {
        var result = await _service.GetTaxReceiptsForProformaAsync(ProformaId);

        result.ShouldAllBe(dto => dto.DocumentType == EDocumentType.TaxReceiptForAdvance);
    }
}
