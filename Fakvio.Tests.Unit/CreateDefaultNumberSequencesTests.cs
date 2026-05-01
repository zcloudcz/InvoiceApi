using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Fakvio.Infrastructure.Service;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Reflection;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for TenantProvisioningService.CreateDefaultNumberSequencesAsync (private static).
///
/// The method is called during tenant provisioning (Step 7) and ensures that all four
/// document types get a default number sequence in a freshly provisioned tenant schema.
///
/// We call the private method via reflection so we can test its logic in full isolation
/// using an InMemoryDatabase — no real PostgreSQL needed.
/// </summary>
public class CreateDefaultNumberSequencesTests : IDisposable
{
    private readonly TenantDbContext _context;

    // Reflection handle for the private static method under test.
    // Cached here so it's resolved once per test class instance, not per test.
    private static readonly MethodInfo _createDefaultSeqs =
        typeof(TenantProvisioningService)
            .GetMethod(
                "CreateDefaultNumberSequencesAsync",
                BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException(
            "CreateDefaultNumberSequencesAsync not found via reflection. " +
            "Check the method name hasn't been renamed.");

    public CreateDefaultNumberSequencesTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        // Seed a default NumberSequenceFormat so CreateDefaultNumberSequencesAsync
        // has a format to reference. We add it with an explicit Id to match
        // the FK that will be set on the new sequences.
        _context.NumberSequenceFormat.Add(new NumberSequenceFormat
        {
            Id = 1,
            Name = "Yearly format",
            FormatPattern = "yyyyNNN",
            CounterDigits = 3,
            ResetsYearly = true,
            ResetsMonthly = false,
            IsActive = true
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Calls TenantProvisioningService.CreateDefaultNumberSequencesAsync via reflection.
    /// The method signature is:
    ///   private static Task CreateDefaultNumberSequencesAsync(
    ///       TenantDbContext tenantContext, CancellationToken cancellationToken)
    /// </summary>
    private Task InvokeCreateDefaultSequencesAsync(CancellationToken ct = default)
    {
        var task = (Task)_createDefaultSeqs.Invoke(null, [_context, ct])!;
        return task;
    }

    // ─── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDefaultNumberSequences_CreatesAllFourDocumentTypes()
    {
        // Act — first call on empty DB
        await InvokeCreateDefaultSequencesAsync();

        // Assert — one default sequence per document type must exist
        var sequences = await _context.NumberSequence.ToListAsync();
        sequences.Count.ShouldBe(4);

        sequences.ShouldContain(s =>
            s.DocumentType == EDocumentType.Invoice && s.IsDefault,
            "Invoice default sequence must be created");

        sequences.ShouldContain(s =>
            s.DocumentType == EDocumentType.CreditNote && s.IsDefault,
            "CreditNote default sequence must be created");

        sequences.ShouldContain(s =>
            s.DocumentType == EDocumentType.Proforma && s.IsDefault,
            "Proforma default sequence must be created");

        sequences.ShouldContain(s =>
            s.DocumentType == EDocumentType.TaxReceiptForAdvance && s.IsDefault,
            "TaxReceiptForAdvance default sequence must be created");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_Invoice_HasCorrectPrefix()
    {
        await InvokeCreateDefaultSequencesAsync();

        var seq = await _context.NumberSequence
            .FirstAsync(s => s.DocumentType == EDocumentType.Invoice);

        seq.Prefix.ShouldBe("INV-");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_CreditNote_HasCorrectPrefix()
    {
        await InvokeCreateDefaultSequencesAsync();

        var seq = await _context.NumberSequence
            .FirstAsync(s => s.DocumentType == EDocumentType.CreditNote);

        seq.Prefix.ShouldBe("CN-");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_Proforma_HasCorrectPfDashPrefix()
    {
        // Issue #26: prefix must be "PF-" (with hyphen), not "PF"
        await InvokeCreateDefaultSequencesAsync();

        var seq = await _context.NumberSequence
            .FirstAsync(s => s.DocumentType == EDocumentType.Proforma);

        seq.Prefix.ShouldBe("PF-",
            "Proforma default sequence must use 'PF-' prefix per issue #26");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_TaxReceiptForAdvance_HasCorrectDppDashPrefix()
    {
        // Issue #26: prefix must be "DPP-" (daňový doklad o přijaté platbě), not "ZF"
        await InvokeCreateDefaultSequencesAsync();

        var seq = await _context.NumberSequence
            .FirstAsync(s => s.DocumentType == EDocumentType.TaxReceiptForAdvance);

        seq.Prefix.ShouldBe("DPP-",
            "TaxReceiptForAdvance default sequence must use 'DPP-' prefix per issue #26");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_AllSequences_StartWithCounterZero()
    {
        // All freshly created sequences start with counter 0 (first document gets number 1).
        await InvokeCreateDefaultSequencesAsync();

        var sequences = await _context.NumberSequence.ToListAsync();

        sequences.ShouldAllBe(s => s.CurrentNumber == 0,
            "All provisioning-created sequences must start with CurrentNumber = 0");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_AllSequences_AreActiveAndDefault()
    {
        // Freshly provisioned sequences must be active and marked as default.
        await InvokeCreateDefaultSequencesAsync();

        var sequences = await _context.NumberSequence.ToListAsync();

        sequences.ShouldAllBe(s => s.IsActive,
            "All provisioned default sequences must be active");

        sequences.ShouldAllBe(s => s.IsDefault,
            "All provisioned sequences must be marked as default (one per document type)");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_AllSequences_ReferenceTheDefaultFormat()
    {
        // All sequences must point to the format we seeded (Id=1).
        await InvokeCreateDefaultSequencesAsync();

        var sequences = await _context.NumberSequence.ToListAsync();

        sequences.ShouldAllBe(s => s.NumberSequenceFormatId == 1,
            "All provisioned sequences must reference the default format");
    }

    // ─── Idempotency ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDefaultNumberSequences_CalledTwice_DoesNotDuplicateSequences()
    {
        // Idempotency guard: re-provisioning (e.g., after partial failure) must not
        // insert a second set of default sequences.
        await InvokeCreateDefaultSequencesAsync();
        await InvokeCreateDefaultSequencesAsync(); // second call

        var sequences = await _context.NumberSequence.ToListAsync();

        // Still exactly four sequences — no duplicates
        sequences.Count.ShouldBe(4,
            "Calling CreateDefaultNumberSequencesAsync twice must not create duplicate sequences");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_WhenInvoiceAlreadyExists_OnlyCreatesOtherThree()
    {
        // Simulates the case where CopyCodeTablesAsync already seeded an Invoice sequence
        // (because master DB had one), but not the others.
        _context.NumberSequence.Add(new NumberSequence
        {
            Name = "Existing Invoice",
            DocumentType = EDocumentType.Invoice,
            Prefix = "INV-",
            CurrentNumber = 10,
            IsDefault = true,
            IsActive = true,
            NumberSequenceFormatId = 1
        });
        await _context.SaveChangesAsync();

        // Act — CreateDefaultNumberSequences should detect the existing Invoice sequence
        // and skip it, while creating the missing CreditNote / Proforma / TaxReceipt ones.
        await InvokeCreateDefaultSequencesAsync();

        var sequences = await _context.NumberSequence.ToListAsync();

        // Four total (1 pre-existing Invoice + 3 newly created)
        sequences.Count.ShouldBe(4);

        // Pre-existing Invoice sequence untouched — counter stays at 10
        sequences.Single(s => s.DocumentType == EDocumentType.Invoice)
            .CurrentNumber.ShouldBe(10,
            "Pre-existing Invoice sequence must not be overwritten");
    }

    // ─── No format available ────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDefaultNumberSequences_NoActiveFormat_CreatesNoSequences()
    {
        // If CopyCodeTablesAsync hasn't yet added any NumberSequenceFormat rows,
        // CreateDefaultNumberSequencesAsync should skip creation gracefully.

        // Remove the format seeded in the constructor
        var format = await _context.NumberSequenceFormat.FindAsync(1L);
        _context.NumberSequenceFormat.Remove(format!);
        await _context.SaveChangesAsync();

        // Act — no format available → must not throw
        await InvokeCreateDefaultSequencesAsync();

        // Assert — no sequences created (admin must add a format first)
        var count = await _context.NumberSequence.CountAsync();
        count.ShouldBe(0, "Without a NumberSequenceFormat, no sequences should be created");
    }

    [Fact]
    public async Task CreateDefaultNumberSequences_OnlyInactiveFormat_CreatesNoSequences()
    {
        // Formats marked IsActive=false are ignored — the method looks for the first ACTIVE format.

        // Deactivate the only format
        var format = await _context.NumberSequenceFormat.FindAsync(1L);
        format!.IsActive = false;
        await _context.SaveChangesAsync();

        await InvokeCreateDefaultSequencesAsync();

        var count = await _context.NumberSequence.CountAsync();
        count.ShouldBe(0, "Inactive formats must not be used to create default sequences");
    }
}
