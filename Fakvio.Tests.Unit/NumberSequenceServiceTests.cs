using Fakvio.Contracts.Dto.NumberSequence;
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
/// Unit tests for NumberSequenceService.
/// Covers the new UpdateFormatAsync and UpdateSequenceAsync methods,
/// as well as basic CRUD and number generation logic.
/// Each test uses an isolated in-memory database for clean state.
/// </summary>
public class NumberSequenceServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly NumberSequenceService _service;
    private readonly ILogger<NumberSequenceService> _logger;

    public NumberSequenceServiceTests()
    {
        // Setup in-memory tenant database — unique name per test class instance for isolation
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        // Setup in-memory master database (required by multi-tenant service constructor)
        var masterOptions = new DbContextOptionsBuilder<MasterDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _masterContext = new MasterDbContext(masterOptions);

        // Mock ITenantResolver to simulate a tenant context — returns companyId 1L
        // so the service uses TenantDbContext (existing test behavior)
        var tenantResolver = Substitute.For<ITenantResolver>();
        tenantResolver.GetCurrentCompanyId().Returns(1L);

        _logger = Substitute.For<ILogger<NumberSequenceService>>();
        _service = new NumberSequenceService(_context, _masterContext, tenantResolver, _logger);

        // Seed initial test data (formats and sequences)
        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _masterContext.Database.EnsureDeleted();
        _masterContext.Dispose();
    }

    /// <summary>
    /// Seeds the in-memory database with test formats and sequences.
    /// </summary>
    private void SeedTestData()
    {
        var format1 = new NumberSequenceFormat
        {
            Id = 1,
            Name = "Yearly format",
            FormatPattern = "yyyyNNN",
            CounterDigits = 3,
            ResetsYearly = true,
            ResetsMonthly = false,
            IsActive = true
        };

        var format2 = new NumberSequenceFormat
        {
            Id = 2,
            Name = "Monthly format",
            FormatPattern = "yyMMNNN",
            CounterDigits = 3,
            ResetsYearly = true,
            ResetsMonthly = true,
            IsActive = true
        };

        _context.NumberSequenceFormat.AddRange(format1, format2);

        var sequence1 = new NumberSequence
        {
            Id = 1,
            Name = "Invoice sequence",
            DocumentType = EDocumentType.Invoice,
            Prefix = "INV-",
            Suffix = null,
            CurrentNumber = 5,
            IsDefault = true,
            NumberSequenceFormatId = 1,
            IsActive = true
        };

        var sequence2 = new NumberSequence
        {
            Id = 2,
            Name = "Credit note sequence",
            DocumentType = EDocumentType.CreditNote,
            Prefix = "CN-",
            Suffix = null,
            CurrentNumber = 2,
            IsDefault = true,
            NumberSequenceFormatId = 1,
            IsActive = true
        };

        _context.NumberSequence.AddRange(sequence1, sequence2);
        _context.SaveChanges();
    }

    // ==================== UpdateFormatAsync Tests ====================

    [Fact]
    public async Task UpdateFormatAsync_WithValidName_UpdatesName()
    {
        // Arrange — update only the name, leave pattern unchanged
        var updateDto = new UpdateNumberSequenceFormatDto { Name = "Updated yearly format" };

        // Act
        var result = await _service.UpdateFormatAsync(1, updateDto);

        // Assert — name should be updated, pattern and other fields remain the same
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Updated yearly format");
        result.FormatPattern.ShouldBe("yyyyNNN"); // Unchanged
        result.CounterDigits.ShouldBe(3); // Unchanged
    }

    [Fact]
    public async Task UpdateFormatAsync_WithValidPattern_UpdatesPatternAndDerivedFields()
    {
        // Arrange — change the pattern, which should also update derived fields
        var updateDto = new UpdateNumberSequenceFormatDto { FormatPattern = "yyMMNNNN" };

        // Act
        var result = await _service.UpdateFormatAsync(1, updateDto);

        // Assert — pattern updated, derived fields recalculated
        result.ShouldNotBeNull();
        result!.FormatPattern.ShouldBe("yyMMNNNN");
        result.CounterDigits.ShouldBe(4); // 4 N's in new pattern
        result.ResetsYearly.ShouldBeTrue(); // yy present
        result.ResetsMonthly.ShouldBeTrue(); // MM present
    }

    [Fact]
    public async Task UpdateFormatAsync_WithInvalidPattern_ThrowsInvalidOperationException()
    {
        // Arrange — a pattern without any N (counter) is invalid
        var updateDto = new UpdateNumberSequenceFormatDto { FormatPattern = "yyyy" };

        // Act & Assert
        var act = () => _service.UpdateFormatAsync(1, updateDto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("Invalid format pattern");
    }

    [Fact]
    public async Task UpdateFormatAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var updateDto = new UpdateNumberSequenceFormatDto { Name = "Does not exist" };

        // Act
        var result = await _service.UpdateFormatAsync(999, updateDto);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateFormatAsync_WithNullFields_DoesNotChangeExistingValues()
    {
        // Arrange — both Name and FormatPattern are null (no changes)
        var updateDto = new UpdateNumberSequenceFormatDto();

        // Act
        var result = await _service.UpdateFormatAsync(1, updateDto);

        // Assert — all original values should be preserved
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Yearly format");
        result.FormatPattern.ShouldBe("yyyyNNN");
    }

    // ==================== UpdateSequenceAsync Tests ====================

    [Fact]
    public async Task UpdateSequenceAsync_WithValidName_UpdatesName()
    {
        // Arrange
        var updateDto = new UpdateNumberSequenceDto { Name = "Main invoice sequence" };

        // Act
        var result = await _service.UpdateSequenceAsync(1, updateDto);

        // Assert
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("Main invoice sequence");
        result.Prefix.ShouldBe("INV-"); // Unchanged
    }

    [Fact]
    public async Task UpdateSequenceAsync_WithNewPrefix_UpdatesPrefix()
    {
        // Arrange — change prefix from "INV-" to "FAK-"
        var updateDto = new UpdateNumberSequenceDto { Prefix = "FAK-" };

        // Act
        var result = await _service.UpdateSequenceAsync(1, updateDto);

        // Assert
        result.ShouldNotBeNull();
        result!.Prefix.ShouldBe("FAK-");
        result.Name.ShouldBe("Invoice sequence"); // Unchanged
    }

    [Fact]
    public async Task UpdateSequenceAsync_WithEmptyPrefix_ClearsPrefix()
    {
        // Arrange — setting prefix to empty string should clear it
        var updateDto = new UpdateNumberSequenceDto { Prefix = "" };

        // Act
        var result = await _service.UpdateSequenceAsync(1, updateDto);

        // Assert
        result.ShouldNotBeNull();
        result!.Prefix.ShouldBeEmpty();
    }

    [Fact]
    public async Task UpdateSequenceAsync_WithNewSuffix_UpdatesSuffix()
    {
        // Arrange — add a suffix to a sequence that didn't have one
        var updateDto = new UpdateNumberSequenceDto { Suffix = "-CZ" };

        // Act
        var result = await _service.UpdateSequenceAsync(1, updateDto);

        // Assert
        result.ShouldNotBeNull();
        result!.Suffix.ShouldBe("-CZ");
    }

    [Fact]
    public async Task UpdateSequenceAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var updateDto = new UpdateNumberSequenceDto { Name = "Ghost" };

        // Act
        var result = await _service.UpdateSequenceAsync(999, updateDto);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateSequenceAsync_DoesNotChangeCurrentNumber()
    {
        // Arrange — update name, verify CurrentNumber is unchanged
        var updateDto = new UpdateNumberSequenceDto { Name = "Renamed" };

        // Act
        var result = await _service.UpdateSequenceAsync(1, updateDto);

        // Assert — CurrentNumber must remain 5 (what we seeded)
        result.ShouldNotBeNull();
        result!.CurrentNumber.ShouldBe(5);
    }

    [Fact]
    public async Task UpdateSequenceAsync_DoesNotChangeDocumentType()
    {
        // Arrange — update name, verify DocumentType stays the same
        var updateDto = new UpdateNumberSequenceDto { Name = "Renamed" };

        // Act
        var result = await _service.UpdateSequenceAsync(1, updateDto);

        // Assert — should still be Invoice type
        result.ShouldNotBeNull();
        result!.DocumentType.ShouldBe(EDocumentType.Invoice);
    }

    // ==================== Create + Get Tests (basic coverage) ====================

    [Fact]
    public async Task CreateFormatAsync_WithValidPattern_ReturnsCreatedFormat()
    {
        // Arrange
        var createDto = new CreateNumberSequenceFormatDto
        {
            Name = "Continuous format",
            FormatPattern = "NNNNN"
        };

        // Act
        var result = await _service.CreateFormatAsync(createDto);

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Continuous format");
        result.FormatPattern.ShouldBe("NNNNN");
        result.CounterDigits.ShouldBe(5);
        result.ResetsYearly.ShouldBeFalse(); // No year in pattern
        result.ResetsMonthly.ShouldBeFalse(); // No month in pattern
    }

    [Fact]
    public async Task GetAllFormatsAsync_IncludeInactive_ReturnsAll()
    {
        // Act
        var result = await _service.GetAllFormatsAsync(includeInactive: true);

        // Assert — we seeded 2 formats
        result.Count.ShouldBe(2);
    }

    [Fact]
    public async Task GetAllSequencesAsync_FilterByDocumentType_ReturnsFiltered()
    {
        // Act
        var invoiceSequences = await _service.GetAllSequencesAsync(
            documentType: EDocumentType.Invoice, includeInactive: true);

        // Assert — we seeded 1 invoice sequence
        invoiceSequences.Count.ShouldBe(1);
        invoiceSequences[0].DocumentType.ShouldBe(EDocumentType.Invoice);
    }

    [Fact]
    public async Task PreviewNextNumberAsync_ReturnsCorrectPreview()
    {
        // Arrange — sequence 1 has pattern "yyyyNNN", prefix "INV-", current number 5
        var issueDate = new DateTime(2026, 2, 1);

        // Act
        var preview = await _service.PreviewNextNumberAsync(1, issueDate);

        // Assert — next number is 6 (5+1), formatted as "INV-2026006"
        preview.ShouldBe("INV-2026006");
    }

    [Fact]
    public async Task DeactivateSequenceAsync_DefaultSequence_ThrowsInvalidOperationException()
    {
        // Act & Assert — cannot deactivate a default sequence
        var act = () => _service.DeactivateSequenceAsync(1);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("default");
    }

    // ==================== PreviewNextNumberForDocumentTypeAsync Tests ====================

    [Fact]
    public async Task PreviewNextNumberForDocumentTypeAsync_WithDefaultSequence_ReturnsPreview()
    {
        // Arrange — Invoice type has default sequence (Id=1, prefix "INV-", pattern "yyyyNNN", current=5)
        var issueDate = new DateTime(2026, 2, 1);

        // Act
        var preview = await _service.PreviewNextNumberForDocumentTypeAsync(EDocumentType.Invoice, issueDate);

        // Assert — next number is 6 (5+1), formatted as "INV-2026006"
        preview.ShouldNotBeNull();
        preview.ShouldBe("INV-2026006");
    }

    [Fact]
    public async Task PreviewNextNumberForDocumentTypeAsync_CreditNote_ReturnsCreditNotePreview()
    {
        // Arrange — CreditNote type has default sequence (Id=2, prefix "CN-", pattern "yyyyNNN", current=2)
        var issueDate = new DateTime(2026, 3, 15);

        // Act
        var preview = await _service.PreviewNextNumberForDocumentTypeAsync(EDocumentType.CreditNote, issueDate);

        // Assert — next number is 3 (2+1), formatted as "CN-2026003"
        preview.ShouldNotBeNull();
        preview.ShouldBe("CN-2026003");
    }

    [Fact]
    public async Task PreviewNextNumberForDocumentTypeAsync_NoDefaultSequence_ReturnsNull()
    {
        // Arrange — Remove all default sequences for Invoice type
        var invoiceSeq = await _context.NumberSequence.FindAsync(1L);
        invoiceSeq!.IsDefault = false;
        await _context.SaveChangesAsync();

        // Act — no default sequence exists for Invoice
        var preview = await _service.PreviewNextNumberForDocumentTypeAsync(
            EDocumentType.Invoice, DateTime.Now);

        // Assert — should return null gracefully (not throw)
        preview.ShouldBeNull();
    }

    [Fact]
    public async Task PreviewNextNumberForDocumentTypeAsync_DoesNotIncrementCounter()
    {
        // Arrange — record current number before preview
        var sequenceBefore = await _context.NumberSequence.AsNoTracking().FirstAsync(s => s.Id == 1);
        var currentNumberBefore = sequenceBefore.CurrentNumber;

        // Act — call preview (should NOT increment)
        await _service.PreviewNextNumberForDocumentTypeAsync(EDocumentType.Invoice, DateTime.Now);

        // Assert — counter should remain unchanged
        var sequenceAfter = await _context.NumberSequence.AsNoTracking().FirstAsync(s => s.Id == 1);
        sequenceAfter.CurrentNumber.ShouldBe(currentNumberBefore);
    }

    // ==================== Format validation Tests ====================

    [Fact]
    public async Task ValidateFormatPattern_WithValidPattern_ReturnsTrue()
    {
        // Act & Assert
        _service.ValidateFormatPattern("yyyyNNN").ShouldBeTrue();
        _service.ValidateFormatPattern("yyMMNNNN").ShouldBeTrue();
        _service.ValidateFormatPattern("NNN").ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateFormatPattern_WithInvalidPattern_ReturnsFalse()
    {
        // Act & Assert — no N's, invalid year length, etc.
        _service.ValidateFormatPattern("yyyy").ShouldBeFalse(); // No counter
        _service.ValidateFormatPattern("").ShouldBeFalse(); // Empty
        _service.ValidateFormatPattern("yyyNNN").ShouldBeFalse(); // 3-digit year not allowed
    }

    // ==================== Proforma + TaxReceiptForAdvance NumberSequence Tests (#26) ====================

    /// <summary>
    /// Seeds a default Proforma number sequence into the in-memory database.
    /// Used by tests that need a Proforma sequence to exist.
    /// </summary>
    private void SeedProformaSequence()
    {
        _context.NumberSequence.Add(new NumberSequence
        {
            Id = 3,
            Name = "Proforma sequence",
            DocumentType = EDocumentType.Proforma,
            Prefix = "PF-",
            Suffix = null,
            CurrentNumber = 0,
            IsDefault = true,
            NumberSequenceFormatId = 1,   // Yearly format from SeedTestData()
            IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Seeds a default TaxReceiptForAdvance number sequence into the in-memory database.
    /// </summary>
    private void SeedTaxReceiptSequence()
    {
        _context.NumberSequence.Add(new NumberSequence
        {
            Id = 4,
            Name = "Tax receipt for advance sequence",
            DocumentType = EDocumentType.TaxReceiptForAdvance,
            Prefix = "DPP-",
            Suffix = null,
            CurrentNumber = 0,
            IsDefault = true,
            NumberSequenceFormatId = 1,   // Yearly format from SeedTestData()
            IsActive = true
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task GenerateNextNumberForDocumentTypeAsync_Proforma_GeneratesCorrectNumber()
    {
        // Arrange
        SeedProformaSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Act — generate first proforma number (counter starts at 0, increments to 1)
        var number = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Proforma, issueDate);

        // Assert — format "yyyyNNN" with prefix "PF-" → "PF-2026001"
        number.ShouldBe("PF-2026001");
    }

    [Fact]
    public async Task GenerateNextNumberForDocumentTypeAsync_Proforma_IncrementsCounter()
    {
        // Arrange — generate first number, then generate a second
        SeedProformaSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Act
        var first = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Proforma, issueDate);
        var second = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Proforma, issueDate);

        // Assert — counter increments by 1 on each call
        first.ShouldBe("PF-2026001");
        second.ShouldBe("PF-2026002");
    }

    [Fact]
    public async Task GenerateNextNumberForDocumentTypeAsync_TaxReceiptForAdvance_GeneratesCorrectNumber()
    {
        // Arrange
        SeedTaxReceiptSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Act — generate first DPP number
        var number = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.TaxReceiptForAdvance, issueDate);

        // Assert — format "yyyyNNN" with prefix "DPP-" → "DPP-2026001"
        number.ShouldBe("DPP-2026001");
    }

    [Fact]
    public async Task GenerateNextNumberForDocumentTypeAsync_TaxReceiptForAdvance_IncrementsCounter()
    {
        // Arrange
        SeedTaxReceiptSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Act — two consecutive generations
        var first = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.TaxReceiptForAdvance, issueDate);
        var second = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.TaxReceiptForAdvance, issueDate);

        // Assert
        first.ShouldBe("DPP-2026001");
        second.ShouldBe("DPP-2026002");
    }

    [Fact]
    public async Task GenerateNextNumberForDocumentTypeAsync_ProformaCounterIsIndependentOfInvoice()
    {
        // Arrange — generate some invoice numbers first, then check proforma starts at 1
        SeedProformaSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Generate 3 invoice numbers to advance the invoice counter
        await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Invoice, issueDate);
        await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Invoice, issueDate);
        await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Invoice, issueDate);

        // Act — generate proforma number (its counter is separate from invoice)
        var proformaNumber = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Proforma, issueDate);

        // Assert — proforma uses its own counter, not the invoice counter
        proformaNumber.ShouldBe("PF-2026001");
    }

    [Fact]
    public async Task GenerateNextNumberForDocumentTypeAsync_MissingProformaDefault_ThrowsWithClearMessage()
    {
        // Arrange — no Proforma sequence seeded (only Invoice and CreditNote from SeedTestData)

        // Act & Assert — expect a clear error message
        var act = () => _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Proforma, DateTime.Now);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("Proforma", Case.Insensitive);
    }

    [Fact]
    public async Task GenerateNextNumberForDocumentTypeAsync_MissingTaxReceiptDefault_ThrowsWithClearMessage()
    {
        // Arrange — no TaxReceiptForAdvance sequence seeded

        // Act & Assert
        var act = () => _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.TaxReceiptForAdvance, DateTime.Now);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("TaxReceiptForAdvance", Case.Insensitive);
    }

    [Fact]
    public async Task PreviewNextNumberForDocumentTypeAsync_Proforma_ReturnsCorrectPreview()
    {
        // Arrange — counter starts at 0; preview simulates next = 1
        SeedProformaSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Act
        var preview = await _service.PreviewNextNumberForDocumentTypeAsync(EDocumentType.Proforma, issueDate);

        // Assert — preview should not change counter, return "PF-2026001"
        preview.ShouldNotBeNull();
        preview.ShouldBe("PF-2026001");
    }

    [Fact]
    public async Task PreviewNextNumberForDocumentTypeAsync_TaxReceiptForAdvance_ReturnsCorrectPreview()
    {
        // Arrange
        SeedTaxReceiptSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Act
        var preview = await _service.PreviewNextNumberForDocumentTypeAsync(EDocumentType.TaxReceiptForAdvance, issueDate);

        // Assert
        preview.ShouldNotBeNull();
        preview.ShouldBe("DPP-2026001");
    }

    [Fact]
    public async Task GetDefaultSequenceAsync_Proforma_ReturnsProformaSequence()
    {
        // Arrange
        SeedProformaSequence();

        // Act
        var dto = await _service.GetDefaultSequenceAsync(EDocumentType.Proforma);

        // Assert — default Proforma sequence is returned with correct prefix
        dto.ShouldNotBeNull();
        dto!.DocumentType.ShouldBe(EDocumentType.Proforma);
        dto.Prefix.ShouldBe("PF-");
        dto.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task GetDefaultSequenceAsync_TaxReceiptForAdvance_ReturnsTaxReceiptSequence()
    {
        // Arrange
        SeedTaxReceiptSequence();

        // Act
        var dto = await _service.GetDefaultSequenceAsync(EDocumentType.TaxReceiptForAdvance);

        // Assert
        dto.ShouldNotBeNull();
        dto!.DocumentType.ShouldBe(EDocumentType.TaxReceiptForAdvance);
        dto.Prefix.ShouldBe("DPP-");
        dto.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task AllFourDocumentTypes_CanGenerateNumbersIndependently()
    {
        // Arrange — seed all four document type sequences
        SeedProformaSequence();
        SeedTaxReceiptSequence();
        var issueDate = new DateTime(2026, 5, 1);

        // Act — generate one number per document type
        var invoiceNum = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Invoice, issueDate);
        var creditNum  = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.CreditNote, issueDate);
        var proNum     = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.Proforma, issueDate);
        var dppNum     = await _service.GenerateNextNumberForDocumentTypeAsync(EDocumentType.TaxReceiptForAdvance, issueDate);

        // Assert — each type uses its own prefix and counter, seeded current numbers are:
        //   Invoice CurrentNumber=5 → next=6, formatted as "INV-2026006"
        //   CreditNote CurrentNumber=2 → next=3, formatted as "CN-2026003"
        //   Proforma CurrentNumber=0 → next=1, formatted as "PF-2026001"
        //   TaxReceiptForAdvance CurrentNumber=0 → next=1, formatted as "DPP-2026001"
        invoiceNum.ShouldBe("INV-2026006");
        creditNum.ShouldBe("CN-2026003");
        proNum.ShouldBe("PF-2026001");
        dppNum.ShouldBe("DPP-2026001");
    }
}
