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
}
