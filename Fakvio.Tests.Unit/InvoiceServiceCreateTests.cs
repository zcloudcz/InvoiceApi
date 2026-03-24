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
/// Unit tests for InvoiceService.CreateInvoiceAsync and related CRUD operations.
/// Covers invoice creation, document number generation, VariableSymbol auto-fill,
/// and validation rules for clients, issuers, and credit notes.
/// </summary>
public class InvoiceServiceCreateTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;
    private readonly ILogger<InvoiceService> _logger;
    private readonly INumberSequenceService _numberSequence;

    public InvoiceServiceCreateTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _logger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();

        // Default: return a document number with mixed characters for VS extraction tests
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV-2026-042");

        _service = new InvoiceService(_context, _numberSequence, _logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds required reference entities — customer, issuer (non-VAT payer), currency.
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

        // VAT payer issuer for VAT validation tests
        _context.Client.Add(new Client
        {
            Id = 3, CompanyName = "VAT Company", RegistrationNumber = "REG003",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = 1, Code = "CZK", Name = "Czech Koruna", Symbol = "Kc",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.SaveChanges();

        _context.VatRate.Add(new VatRate
        {
            Id = 1, Name = "Standard 21%", Rate = 21, IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Helper: creates a valid CreateInvoiceDto with one item.
    /// </summary>
    private CreateInvoiceDto CreateValidInvoiceDto()
    {
        return new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Consulting services",
                    Quantity = 10,
                    Unit = "hrs",
                    UnitPrice = 150
                }
            }
        };
    }

    // =====================================================================
    // CreateInvoiceAsync — happy path
    // =====================================================================

    [Fact]
    public async Task CreateInvoiceAsync_ValidDto_ShouldCreateDraftInvoice()
    {
        // Arrange
        var dto = CreateValidInvoiceDto();

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        result.ShouldNotBeNull();
        result.Status.ShouldBe(EInvoiceStatus.Draft);
        result.ClientId.ShouldBe(1);
        result.IssuerId.ShouldBe(2);
        result.DocumentType.ShouldBe(EDocumentType.Invoice);
        result.InvoiceItem.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CreateInvoiceAsync_ShouldCalculateTotalsCorrectly()
    {
        // Arrange — 10 hrs * 150 = 1500 before VAT (no VAT for non-VAT payer)
        var dto = CreateValidInvoiceDto();

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        result.TotalBeforeVat.ShouldBe(1500);
        result.TotalVat.ShouldBe(0); // Non-VAT payer, no VatRateId set
        result.TotalWithVat.ShouldBe(1500);
    }

    [Fact]
    public async Task CreateInvoiceAsync_ShouldAutoGenerateDocumentNumber()
    {
        // Arrange
        var dto = CreateValidInvoiceDto();

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — document number from mock: "INV-2026-042"
        result.DocumentNumber.ShouldBe("INV-2026-042");
    }

    [Fact]
    public async Task CreateInvoiceAsync_CustomDocumentNumber_ShouldUseProvided()
    {
        // Arrange
        var dto = CreateValidInvoiceDto();
        dto.CustomDocumentNumber = "CUSTOM-001";

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — custom number is used, no auto-generation
        result.DocumentNumber.ShouldBe("CUSTOM-001");
    }

    // =====================================================================
    // VariableSymbol auto-fill — digits only, max 10
    // =====================================================================

    [Fact]
    public async Task CreateInvoiceAsync_VariableSymbol_ShouldAutoFillDigitsOnly()
    {
        // Arrange — doc number "INV-2026-042" → digits: "2026042"
        var dto = CreateValidInvoiceDto();

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — only digits extracted, separators stripped
        result.VariableSymbol.ShouldBe("2026042");
        result.VariableSymbol!.All(char.IsDigit).ShouldBeTrue();
    }

    [Fact]
    public async Task CreateInvoiceAsync_VariableSymbol_ShouldTruncateTo10Digits()
    {
        // Arrange — setup mock to return a long document number with many digits
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV-20260102-9999999999");

        var dto = CreateValidInvoiceDto();

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — digits: "2026010299999999" → Take(10) → "2026010299"
        result.VariableSymbol!.Length.ShouldBeLessThanOrEqualTo(10);
    }

    [Fact]
    public async Task CreateInvoiceAsync_ExplicitVariableSymbol_ShouldNotAutoFill()
    {
        // Arrange — user provides their own variable symbol
        var dto = CreateValidInvoiceDto();
        dto.VariableSymbol = "1234567890";

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — explicitly provided value should be preserved
        result.VariableSymbol.ShouldBe("1234567890");
    }

    // =====================================================================
    // Validation tests
    // =====================================================================

    [Fact]
    public async Task CreateInvoiceAsync_InvalidClientId_ShouldThrow()
    {
        // Arrange
        var dto = CreateValidInvoiceDto();
        dto.ClientId = 999;

        // Act & Assert
        var act = () => _service.CreateInvoiceAsync(dto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("Client");
        ex.Message.ShouldContain("999");
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task CreateInvoiceAsync_InvalidIssuerId_ShouldThrow()
    {
        // Arrange — use a non-issuer client as issuer
        var dto = CreateValidInvoiceDto();
        dto.IssuerId = 1; // Client 1 is a customer, not an issuer

        // Act & Assert
        var act = () => _service.CreateInvoiceAsync(dto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("Issuer");
        ex.Message.ShouldContain("1");
        ex.Message.ShouldContain("not found or is not marked as issuer");
    }

    [Fact]
    public async Task CreateInvoiceAsync_CreditNoteWithoutOriginalInvoice_ShouldThrow()
    {
        // Arrange
        var dto = CreateValidInvoiceDto();
        dto.DocumentType = EDocumentType.CreditNote;
        dto.OriginalInvoiceId = null; // Missing required field

        // Act & Assert
        var act = () => _service.CreateInvoiceAsync(dto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("OriginalInvoiceId is required");
    }

    // =====================================================================
    // Delete and status transitions
    // =====================================================================

    [Fact]
    public async Task DeleteInvoiceAsync_DraftInvoice_ShouldSoftDelete()
    {
        // Arrange
        var invoice = await _service.CreateInvoiceAsync(CreateValidInvoiceDto());

        // Act
        var deleted = await _service.DeleteInvoiceAsync(invoice.Id);

        // Assert
        deleted.ShouldBeTrue();

        // After soft delete, GetInvoiceByIdAsync still returns the invoice
        // (so users can view details and restore it), but with Deleted status.
        var loaded = await _service.GetInvoiceByIdAsync(invoice.Id);
        loaded.ShouldNotBeNull();
        loaded.Status.ShouldBe(EInvoiceStatus.Deleted);
    }

    [Fact]
    public async Task DeleteInvoiceAsync_LastCompletedInvoice_ShouldSucceed()
    {
        // Arrange — create and complete an invoice (it's the only one → last in sequence)
        var invoice = await _service.CreateInvoiceAsync(CreateValidInvoiceDto());
        await _service.CompleteInvoiceAsync(invoice.Id);

        // Act — the last completed invoice CAN be deleted
        var result = await _service.DeleteInvoiceAsync(invoice.Id);

        // Assert — deletion should succeed because it's the last issued invoice
        result.ShouldBeTrue();
    }

    // =====================================================================
    // Variable Symbol duplicate check
    // =====================================================================

    [Fact]
    public async Task CreateInvoiceAsync_DuplicateVariableSymbol_ShouldThrow()
    {
        // Arrange — create the first invoice (gets VS from auto-generated document number)
        var firstInvoice = await _service.CreateInvoiceAsync(CreateValidInvoiceDto());
        firstInvoice.VariableSymbol.ShouldNotBeNullOrEmpty();

        // The second invoice will get the same document number from the mock,
        // which means the same VariableSymbol — should throw.

        // Act & Assert
        var act = () => _service.CreateInvoiceAsync(CreateValidInvoiceDto());
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("Variable Symbol");
        ex.Message.ShouldContain("already exists");
    }

    [Fact]
    public async Task CreateInvoiceAsync_DuplicateVsOnDeletedInvoice_ShouldNotThrow()
    {
        // Arrange — create an invoice, then soft-delete it
        var firstInvoice = await _service.CreateInvoiceAsync(CreateValidInvoiceDto());
        firstInvoice.VariableSymbol.ShouldNotBeNullOrEmpty();
        await _service.DeleteInvoiceAsync(firstInvoice.Id);

        // Act — creating a second invoice with the same VS should succeed
        // because the first one is deleted (Status = Deleted is excluded from duplicate check).
        var secondInvoice = await _service.CreateInvoiceAsync(CreateValidInvoiceDto());

        // Assert
        secondInvoice.ShouldNotBeNull();
        secondInvoice.VariableSymbol.ShouldBe(firstInvoice.VariableSymbol);
    }
}
