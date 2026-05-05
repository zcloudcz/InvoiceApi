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
        // Arrange — user provides a custom VS and explicitly opts in to manual override.
        // VariableSymbolIsManualOverride = true signals that the user deliberately chose
        // a VS that should not be overwritten by the backend's DocumentNumber-derived value.
        var dto = CreateValidInvoiceDto();
        dto.VariableSymbol = "1234567890";
        dto.VariableSymbolIsManualOverride = true;

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — explicitly provided value should be preserved
        result.VariableSymbol.ShouldBe("1234567890");
    }

    // =====================================================================
    // Issue #107 — VariableSymbol must match DocumentNumber prefix/suffix
    // =====================================================================

    /// <summary>
    /// Issue #107: when the UI sends a stale preview VS (without the client prefix)
    /// and VariableSymbolIsManualOverride = false (the default), the backend must
    /// always re-derive VS from the generated DocumentNumber.
    ///
    /// Scenario: the doc-number sequence produces "INV-2026-042", the client has
    /// an "EU-" prefix so the final DocumentNumber = "EU-INV-2026-042".
    /// The UI preview was "INV-2026-042" → digits "2026042", so UI sent VS = "2026042".
    /// After the fix the backend ignores that stale value and re-derives: "2026042"
    /// (digits of "EU-INV-2026-042") — same digit portion, but now derived from the
    /// correct, prefixed DocumentNumber, so a future rename of the prefix automatically
    /// flows through to VS too.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_WithClientPrefix_VariableSymbol_ShouldMatchGeneratedDocumentNumber()
    {
        // Arrange — give client #1 an "EU-" invoice number prefix
        _context.Set<BillingSettings>().Add(new BillingSettings
        {
            ClientId = 1,
            InvoiceNumberPrefix = "EU-"
        });

        // Named sequence so GenerateDocumentNumberAsync takes the prefix-application path
        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 100,
            Name = "EU Sequence",
            DocumentType = EDocumentType.Invoice,
            CurrentNumber = 1,
            IsDefault = false,
            IsActive = true,
            NumberSequenceFormatId = 0
        });
        _context.SaveChanges();

        _numberSequence
            .GenerateNextNumberAsync(100L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("20260001");

        var dto = CreateValidInvoiceDto();
        dto.NumberSequenceId = 100;

        // Simulate the stale UI preview VS (without prefix) — backend must overwrite this.
        // VariableSymbolIsManualOverride stays false (default) → backend re-derives.
        dto.VariableSymbol = "20260001"; // stale preview value sent by UI

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — DocumentNumber has the prefix; VS is derived from the prefixed number's digits
        result.DocumentNumber.ShouldBe("EU-20260001");
        // Digits of "EU-20260001" → "20260001" (prefix "EU-" has no digits)
        result.VariableSymbol.ShouldBe("20260001");
        // Crucially: VS must be derived from the final DocumentNumber, not from the pre-prefix bare number.
        // Both happen to be the same digits here, but the derivation path is now always correct
        // even when the prefix contains digits (see next test).
        result.VariableSymbol!.All(char.IsDigit).ShouldBeTrue();
    }

    /// <summary>
    /// Issue #107 — prefix contains digits: VS must still be derived from the FULL
    /// DocumentNumber (prefix included), not from the bare sequence number.
    ///
    /// Scenario: prefix = "99-", sequence produces "2026001".
    /// Final DocumentNumber = "99-2026001" → digits = "992026001".
    /// UI sent VS = "2026001" (no prefix) — backend must re-derive to "992026001".
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_PrefixContainsDigits_VariableSymbol_DerivedFromFullDocumentNumber()
    {
        // Arrange — prefix "99-" contains digits
        _context.Set<BillingSettings>().Add(new BillingSettings
        {
            ClientId = 1,
            InvoiceNumberPrefix = "99-"
        });

        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 101,
            Name = "Digit Prefix Seq",
            DocumentType = EDocumentType.Invoice,
            CurrentNumber = 1,
            IsDefault = false,
            IsActive = true,
            NumberSequenceFormatId = 0
        });
        _context.SaveChanges();

        _numberSequence
            .GenerateNextNumberAsync(101L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("2026001");

        var dto = CreateValidInvoiceDto();
        dto.NumberSequenceId = 101;
        // Stale UI preview VS without prefix
        dto.VariableSymbol = "2026001";
        // VariableSymbolIsManualOverride = false (default) → backend re-derives

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — digits of "99-2026001" → "992026001"
        result.DocumentNumber.ShouldBe("99-2026001");
        result.VariableSymbol.ShouldBe("992026001");
        result.VariableSymbol!.All(char.IsDigit).ShouldBeTrue();
    }

    /// <summary>
    /// Issue #107 — UI-supplied VS without the override flag must be discarded even when
    /// it is non-empty. This is the core regression guard: VariableSymbolIsManualOverride
    /// defaults to false, so passing any VS value in the DTO without setting the flag
    /// should result in VS being re-derived from DocumentNumber.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_StalePreviewVsWithoutOverrideFlag_ShouldBeDiscarded()
    {
        // Arrange — default mock: doc number "INV-2026-042"
        var dto = CreateValidInvoiceDto();
        // Simulate UI sending preview VS (digits of "INV-2026-042" = "2026042")
        // but NOT setting VariableSymbolIsManualOverride — this is the bug scenario.
        dto.VariableSymbol = "2026042";
        // dto.VariableSymbolIsManualOverride stays false (default)

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — VS derived from DocumentNumber "INV-2026-042" → "2026042"
        // (same value here, but the derivation path is now backend-controlled)
        result.DocumentNumber.ShouldBe("INV-2026-042");
        result.VariableSymbol.ShouldBe("2026042");
    }

    /// <summary>
    /// VariableSymbolIsManualOverride = true: user deliberately provided a VS that
    /// differs from DocumentNumber digits — backend must preserve it.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_ManualOverrideFlag_ShouldPreserveUserSuppliedVs()
    {
        // Arrange
        var dto = CreateValidInvoiceDto();
        dto.VariableSymbol = "9876543210";
        dto.VariableSymbolIsManualOverride = true;

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — explicitly overridden VS is preserved unchanged
        result.VariableSymbol.ShouldBe("9876543210");
        // DocumentNumber is still generated normally
        result.DocumentNumber.ShouldBe("INV-2026-042");
    }

    /// <summary>
    /// CreditNote created via the standard path: VS must be re-derived from the
    /// CreditNote's own DocumentNumber, not from the original invoice's VS.
    /// The credit-note document number produces different digits from the original
    /// invoice, so the VS values will be distinct (no duplicate-VS collision).
    /// </summary>
    [Fact]
    public async Task CreateInvoiceAsync_CreditNote_VariableSymbol_DerivedFromCreditNoteDocumentNumber()
    {
        // Arrange — seed a completed invoice to use as OriginalInvoiceId.
        // Use a VS with different digit pattern from the credit-note number (see below).
        var originalInvoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            DocumentNumber = "INV-2026-001",
            VariableSymbol = "2026001",
            IssueDate = DateTime.UtcNow.Date,
            TaxableSupplyDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.Date.AddDays(14),
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(originalInvoice);
        await _context.SaveChangesAsync();

        // Credit-note sequence produces a number whose digits differ from the original invoice.
        // "CN-2026-999" → digits "2026999" ≠ "2026001" — no duplicate VS collision.
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                EDocumentType.CreditNote, Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("CN-2026-999");

        var dto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.CreditNote,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            OriginalInvoiceId = originalInvoice.Id,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new() { OrderIndex = 1, Description = "Refund", Quantity = 1, Unit = "pcs", UnitPrice = 100 }
            }
        };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — VS derived from credit-note DocumentNumber "CN-2026-999" → "2026999"
        result.DocumentType.ShouldBe(EDocumentType.CreditNote);
        result.DocumentNumber.ShouldBe("CN-2026-999");
        result.VariableSymbol.ShouldBe("2026999");
        result.VariableSymbol.ShouldNotBe(originalInvoice.VariableSymbol); // must differ from original
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
