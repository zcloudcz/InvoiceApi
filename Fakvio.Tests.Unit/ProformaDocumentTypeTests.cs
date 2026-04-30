using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for the Proforma and TaxReceiptForAdvance document type model.
///
/// Verifies that:
///   - The new enum values have the correct integer values (never change existing values).
///   - The self-referencing OriginalInvoiceId FK is nullable (no document is forced
///     to have a parent) and uses Restrict on-delete (deleting a parent does not
///     cascade-delete its children — the parent is protected instead).
///   - The existing Invoice and CreditNote enum values are unchanged.
///   - A Proforma and a TaxReceiptForAdvance can be linked via OriginalInvoiceId
///     without any new column.
/// </summary>
public class ProformaDocumentTypeTests : IDisposable
{
    private readonly TenantDbContext _context;

    public ProformaDocumentTypeTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        // Do NOT call EnsureCreated() — that would apply the HasData() seed rows
        // (Currency Id=1, Client, NumberSequence etc.) and then our SeedReferenceData()
        // would attempt to insert with the same PKs, causing a duplicate key error.
        SeedReferenceData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── EDocumentType enum value tests ──────────────────────────────────────

    [Fact]
    public void EDocumentType_Invoice_HasValue1()
    {
        // Existing values must never change — they are stored as integers in the DB.
        ((int)EDocumentType.Invoice).ShouldBe(1);
    }

    [Fact]
    public void EDocumentType_CreditNote_HasValue2()
    {
        ((int)EDocumentType.CreditNote).ShouldBe(2);
    }

    [Fact]
    public void EDocumentType_Proforma_HasValue3()
    {
        ((int)EDocumentType.Proforma).ShouldBe(3);
    }

    [Fact]
    public void EDocumentType_TaxReceiptForAdvance_HasValue4()
    {
        ((int)EDocumentType.TaxReceiptForAdvance).ShouldBe(4);
    }

    // ─── EF Core model / FK configuration tests ───────────────────────────────

    [Fact]
    public void Invoice_OriginalInvoiceId_IsNullableInEfModel()
    {
        // Arrange — get the EF model metadata for the Invoice entity.
        var entityType = _context.Model.FindEntityType(typeof(Invoice));
        entityType.ShouldNotBeNull();

        // Act — find the OriginalInvoiceId property.
        var property = entityType.FindProperty(nameof(Invoice.OriginalInvoiceId));
        property.ShouldNotBeNull("OriginalInvoiceId must be mapped as a property in EF Core");

        // Assert — the FK is nullable so a standalone document (no parent) is valid.
        property.IsNullable.ShouldBeTrue(
            "OriginalInvoiceId must be nullable — pro-forma and standalone invoices have no parent");
    }

    [Fact]
    public void Invoice_OriginalInvoiceFk_UsesRestrictDeleteBehavior()
    {
        // Arrange — locate the self-referencing foreign key on Invoice.
        var entityType = _context.Model.FindEntityType(typeof(Invoice));
        entityType.ShouldNotBeNull();

        // The FK that points OriginalInvoiceId → Invoice.Id
        var fk = entityType.GetForeignKeys()
            .FirstOrDefault(f => f.Properties.Any(p => p.Name == nameof(Invoice.OriginalInvoiceId)));

        fk.ShouldNotBeNull("Self-referencing FK on OriginalInvoiceId must exist in the EF model");

        // Assert — Restrict means the parent cannot be deleted while children reference it.
        // Cascade would silently delete TaxReceiptForAdvance when its proforma is deleted —
        // that would be a data loss bug, so we enforce Restrict here.
        fk.DeleteBehavior.ShouldBe(DeleteBehavior.Restrict,
            "Deleting a parent document must not cascade-delete linked documents");
    }

    // ─── Domain behaviour tests ───────────────────────────────────────────────

    [Fact]
    public void Proforma_CanBeCreated_WithoutOriginalInvoiceId()
    {
        // A standalone pro-forma has no parent document.
        var proforma = BuildInvoice(EDocumentType.Proforma, originalInvoiceId: null);

        proforma.DocumentType.ShouldBe(EDocumentType.Proforma);
        proforma.OriginalInvoiceId.ShouldBeNull();
    }

    [Fact]
    public void TaxReceiptForAdvance_CanReferenceProforma_ViaOriginalInvoiceId()
    {
        // Arrange — persist a Proforma first.
        var proforma = BuildInvoice(EDocumentType.Proforma, originalInvoiceId: null);
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        // Act — create a TaxReceiptForAdvance that links back to the Proforma.
        var taxReceipt = BuildInvoice(EDocumentType.TaxReceiptForAdvance, originalInvoiceId: proforma.Id);
        _context.Invoice.Add(taxReceipt);
        _context.SaveChanges();

        // Assert — reload and verify the link.
        var loaded = _context.Invoice
            .Include(i => i.OriginalInvoice)
            .Single(i => i.Id == taxReceipt.Id);

        loaded.DocumentType.ShouldBe(EDocumentType.TaxReceiptForAdvance);
        loaded.OriginalInvoiceId.ShouldBe(proforma.Id);
        loaded.OriginalInvoice.ShouldNotBeNull();
        loaded.OriginalInvoice!.DocumentType.ShouldBe(EDocumentType.Proforma);
    }

    [Fact]
    public void Proforma_InverseNavigation_ContainsTaxReceipt()
    {
        // Arrange — persist Proforma and a linked TaxReceiptForAdvance.
        var proforma = BuildInvoice(EDocumentType.Proforma, originalInvoiceId: null);
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        var taxReceipt = BuildInvoice(EDocumentType.TaxReceiptForAdvance, originalInvoiceId: proforma.Id);
        _context.Invoice.Add(taxReceipt);
        _context.SaveChanges();

        // Act — load the Proforma with its CreditNote (inverse) collection.
        var loaded = _context.Invoice
            .Include(i => i.CreditNote)
            .Single(i => i.Id == proforma.Id);

        // Assert — the inverse collection contains the TaxReceiptForAdvance.
        // The collection is named "CreditNote" for legacy reasons but holds ALL
        // documents that reference this one as OriginalInvoice.
        loaded.CreditNote.ShouldContain(
            d => d.Id == taxReceipt.Id && d.DocumentType == EDocumentType.TaxReceiptForAdvance);
    }

    // ─── Enum completeness / regression guard ─────────────────────────────────

    [Fact]
    public void EDocumentType_HasExactlyFourValues()
    {
        // Guard against accidental additions that skip the integer-pinning rule.
        // Every new value MUST also get a pinned-integer test above.
        var values = Enum.GetValues<EDocumentType>();
        values.Length.ShouldBe(4, "Add a new pinned-integer test when extending EDocumentType");
    }

    // ─── Seed data tests ──────────────────────────────────────────────────────
    // EF Core 10 uses a read-optimised runtime model that strips HasData() metadata.
    // To verify what HasData() seeds, we create a separate in-memory DB, call
    // EnsureCreated() to apply the seed rows, and query the actual table.
    // A static unique name is safe here because this helper context is discarded
    // inside the test method before any other test could race on the same name.

    private static TenantDbContext CreateSeededContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: $"seed-check-{Guid.NewGuid()}")
            .Options;
        var ctx = new TenantDbContext(options);
        // EnsureCreated() applies all HasData() entries (no real migration needed for InMemory).
        ctx.Database.EnsureCreated();
        return ctx;
    }

    [Fact]
    public void SeedData_ContainsProformaNumberSequence()
    {
        // Verify that the HasData() seed for EDocumentType.Proforma is present.
        // Catches a regression where the seed row is removed without a compensating migration.
        using var ctx = CreateSeededContext();
        var seq = ctx.NumberSequence
            .FirstOrDefault(s => s.DocumentType == EDocumentType.Proforma);

        seq.ShouldNotBeNull("HasData() must seed a NumberSequence for EDocumentType.Proforma");
    }

    [Fact]
    public void SeedData_ContainsTaxReceiptForAdvanceNumberSequence()
    {
        using var ctx = CreateSeededContext();
        var seq = ctx.NumberSequence
            .FirstOrDefault(s => s.DocumentType == EDocumentType.TaxReceiptForAdvance);

        seq.ShouldNotBeNull("HasData() must seed a NumberSequence for EDocumentType.TaxReceiptForAdvance");
    }

    [Fact]
    public void SeedData_ProformaSequence_HasPfPrefixAndIsDefault()
    {
        // 'PF' prefix matches the business convention for pro-forma numbering (PF2025001).
        using var ctx = CreateSeededContext();
        var seq = ctx.NumberSequence
            .First(s => s.DocumentType == EDocumentType.Proforma);

        seq.Prefix.ShouldBe("PF",
            "Proforma number sequence must use the 'PF' prefix");
        seq.IsDefault.ShouldBeTrue(
            "The seeded Proforma sequence must be marked as default");
    }

    [Fact]
    public void SeedData_TaxReceiptSequence_HasZfPrefixAndIsDefault()
    {
        // 'ZF' prefix matches the business convention (záloha faktura).
        using var ctx = CreateSeededContext();
        var seq = ctx.NumberSequence
            .First(s => s.DocumentType == EDocumentType.TaxReceiptForAdvance);

        seq.Prefix.ShouldBe("ZF",
            "TaxReceiptForAdvance number sequence must use the 'ZF' prefix");
        seq.IsDefault.ShouldBeTrue(
            "The seeded TaxReceiptForAdvance sequence must be marked as default");
    }

    // ─── Multi-document link tests ─────────────────────────────────────────────

    [Fact]
    public void Proforma_CanHaveMultipleTaxReceipts_ViaInverseCollection()
    {
        // A single pro-forma can result in partial-payment tax receipts
        // (multiple advances). Verify that the one-to-many model works.
        var proforma = BuildInvoice(EDocumentType.Proforma, originalInvoiceId: null);
        _context.Invoice.Add(proforma);
        _context.SaveChanges();

        var taxReceipt1 = BuildInvoice(EDocumentType.TaxReceiptForAdvance, originalInvoiceId: proforma.Id);
        var taxReceipt2 = BuildInvoice(EDocumentType.TaxReceiptForAdvance, originalInvoiceId: proforma.Id);
        _context.Invoice.AddRange(taxReceipt1, taxReceipt2);
        _context.SaveChanges();

        // Load the pro-forma with its inverse collection.
        var loaded = _context.Invoice
            .Include(i => i.CreditNote)
            .Single(i => i.Id == proforma.Id);

        loaded.CreditNote.Count.ShouldBe(2,
            "A pro-forma must be able to reference multiple TaxReceiptForAdvance documents");
        loaded.CreditNote.ShouldAllBe(d => d.DocumentType == EDocumentType.TaxReceiptForAdvance);
    }

    [Fact]
    public void CreditNote_StillLinksToInvoice_RegressionGuard()
    {
        // Verify that the existing CreditNote → Invoice link is unaffected by
        // adding Proforma and TaxReceiptForAdvance to the same FK / collection.
        var invoice = BuildInvoice(EDocumentType.Invoice, originalInvoiceId: null);
        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        var creditNote = BuildInvoice(EDocumentType.CreditNote, originalInvoiceId: invoice.Id);
        _context.Invoice.Add(creditNote);
        _context.SaveChanges();

        var loaded = _context.Invoice
            .Include(i => i.OriginalInvoice)
            .Single(i => i.Id == creditNote.Id);

        loaded.DocumentType.ShouldBe(EDocumentType.CreditNote);
        loaded.OriginalInvoiceId.ShouldBe(invoice.Id);
        loaded.OriginalInvoice!.DocumentType.ShouldBe(EDocumentType.Invoice);
    }

    [Fact]
    public void Invoice_InverseCollection_SegregatesByDocumentType()
    {
        // When an invoice has both a credit note AND a TaxReceiptForAdvance (via
        // shared OriginalInvoiceId), the inverse collection contains both, and
        // calling code can filter by DocumentType.
        var invoice = BuildInvoice(EDocumentType.Invoice, originalInvoiceId: null);
        _context.Invoice.Add(invoice);
        _context.SaveChanges();

        var creditNote = BuildInvoice(EDocumentType.CreditNote, originalInvoiceId: invoice.Id);
        var taxReceipt = BuildInvoice(EDocumentType.TaxReceiptForAdvance, originalInvoiceId: invoice.Id);
        _context.Invoice.AddRange(creditNote, taxReceipt);
        _context.SaveChanges();

        var loaded = _context.Invoice
            .Include(i => i.CreditNote)
            .Single(i => i.Id == invoice.Id);

        loaded.CreditNote.Count.ShouldBe(2);
        loaded.CreditNote.ShouldContain(d => d.DocumentType == EDocumentType.CreditNote);
        loaded.CreditNote.ShouldContain(d => d.DocumentType == EDocumentType.TaxReceiptForAdvance);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private long _issuerId;
    private long _currencyId;

    /// <summary>
    /// Seeds the minimum reference data required by Invoice FK constraints:
    /// one issuer Client and one Currency.
    /// Uses no explicit PKs — EF generates them via identity columns.
    /// This avoids "duplicate key" conflicts with HasData() seed rows that
    /// InMemoryDatabase applies automatically on the first SaveChanges() call.
    /// </summary>
    private void SeedReferenceData()
    {
        var issuer = new Client
        {
            IsIssuer = true,
            CompanyName = "Test Issuer s.r.o.",
            RegistrationNumber = "99999999",
            Language = "cs",
            IsActive = true
        };
        _context.Client.Add(issuer);
        _context.SaveChanges();
        _issuerId = issuer.Id;

        var currency = new Currency
        {
            Code = "TST",
            Name = "Test Currency",
            Symbol = "T",
            DecimalPlaces = 2,
            IsActive = true,
            SortOrder = 99
        };
        _context.Currency.Add(currency);
        _context.SaveChanges();
        _currencyId = currency.Id;
    }

    /// <summary>
    /// Builds a minimal valid <see cref="Invoice"/> for testing.
    /// All FK-required fields are filled to pass InMemoryDatabase validation.
    /// </summary>
    private Invoice BuildInvoice(EDocumentType type, long? originalInvoiceId)
    {
        return new Invoice
        {
            DocumentType = type,
            Status = EInvoiceStatus.Draft,
            IssuerId = _issuerId,
            CurrencyId = _currencyId,
            OriginalInvoiceId = originalInvoiceId,
            InvoiceItem = new List<InvoiceItem>()
        };
    }
}
