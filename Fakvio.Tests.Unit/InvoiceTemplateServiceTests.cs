using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.InvoiceTemplate;
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
/// Unit tests for InvoiceTemplateService.
/// Covers template CRUD, CreateInvoiceFromTemplate, and CreateTemplateFromInvoice.
/// Uses InMemoryDatabase with manually seeded reference entities.
/// </summary>
public class InvoiceTemplateServiceTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceTemplateService _templateService;
    private readonly InvoiceService _invoiceService;
    private readonly ILogger<InvoiceTemplateService> _templateLogger;
    private readonly ILogger<InvoiceService> _invoiceLogger;
    private readonly INumberSequenceService _numberSequence;

    public InvoiceTemplateServiceTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
        _templateLogger = Substitute.For<ILogger<InvoiceTemplateService>>();
        _invoiceLogger = Substitute.For<ILogger<InvoiceService>>();
        _numberSequence = Substitute.For<INumberSequenceService>();

        // Setup number sequence mock to return a predictable document number
        _numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV2026001");

        // Setup named-sequence mock (used when template has NumberSequenceId set)
        _numberSequence
            .GenerateNextNumberAsync(
                Arg.Any<long>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns("SEQ2026001");

        _invoiceService = new InvoiceService(_context, _numberSequence, Substitute.For<ITenantReadinessService>(), _invoiceLogger);
        _templateService = new InvoiceTemplateService(_context, _invoiceService, _templateLogger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// Seeds required reference entities — client, issuer, currency, VAT rate.
    /// Each entity is saved individually to satisfy InMemoryDb FK resolution.
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

        _context.VatRate.Add(new VatRate
        {
            Id = 1, Name = "Standard 21%", Rate = 21, IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-1), IsActive = true
        });
        _context.SaveChanges();
    }

    /// <summary>
    /// Helper: creates a valid CreateInvoiceTemplateDto with one item.
    /// </summary>
    private CreateInvoiceTemplateDto CreateValidTemplateDto(string name = "Test Template")
    {
        return new CreateInvoiceTemplateDto
        {
            Name = name,
            DocumentType = EDocumentType.Invoice,
            IssuerId = 2,
            CurrencyId = 1,
            DueDateOffsetDays = 14,
            PaymentMethod = EPaymentMethod.BankTransfer,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Hosting services",
                    Quantity = 1,
                    Unit = "month",
                    UnitPrice = 500
                }
            }
        };
    }

    // =====================================================================
    // CreateTemplateAsync tests
    // =====================================================================

    [Fact]
    public async Task CreateTemplateAsync_ValidDto_ShouldCreateAndReturnTemplate()
    {
        // Arrange
        var dto = CreateValidTemplateDto("Monthly Hosting");

        // Act
        var result = await _templateService.CreateTemplateAsync(dto);

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Monthly Hosting");
        result.IssuerId.ShouldBe(2);
        result.DocumentType.ShouldBe(EDocumentType.Invoice);
        result.InvoiceItem.Count.ShouldBe(1);
        result.IsActive.ShouldBeTrue();
        result.UsageCount.ShouldBe(0);
    }

    [Fact]
    public async Task CreateTemplateAsync_ShouldCalculateItemTotals()
    {
        // Arrange
        var dto = CreateValidTemplateDto();
        dto.InvoiceItem = new List<CreateInvoiceItemDto>
        {
            new() { OrderIndex = 1, Description = "Item A", Quantity = 2, Unit = "pcs", UnitPrice = 100 },
            new() { OrderIndex = 2, Description = "Item B", Quantity = 1, Unit = "pcs", UnitPrice = 300 }
        };

        // Act
        var result = await _templateService.CreateTemplateAsync(dto);

        // Assert — TotalBeforeVat = (2*100) + (1*300) = 500 (no VAT since non-VAT payer)
        result.InvoiceItem.Count.ShouldBe(2);
        result.InvoiceItem[0].TotalBeforeVat.ShouldBe(200);
        result.InvoiceItem[1].TotalBeforeVat.ShouldBe(300);
    }

    [Fact]
    public async Task CreateTemplateAsync_InvalidIssuer_ShouldThrow()
    {
        // Arrange — IssuerId 999 does not exist
        var dto = CreateValidTemplateDto();
        dto.IssuerId = 999;

        // Act & Assert
        var act = () => _templateService.CreateTemplateAsync(dto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("Issuer");
        ex.Message.ShouldContain("999");
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task CreateTemplateAsync_WithNumberSequenceId_ShouldPersist()
    {
        // Arrange — create a number sequence first
        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 1, Name = "Custom Seq", DocumentType = EDocumentType.Invoice,
            CurrentNumber = 1, IsDefault = false, IsActive = true,
            NumberSequenceFormatId = 0 // Not needed for this test
        });
        _context.SaveChanges();

        var dto = CreateValidTemplateDto();
        dto.NumberSequenceId = 1;

        // Act
        var result = await _templateService.CreateTemplateAsync(dto);

        // Assert
        result.NumberSequenceId.ShouldBe(1);
    }

    // =====================================================================
    // CreateInvoiceFromTemplateAsync tests
    // =====================================================================

    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_ValidTemplate_ShouldCreateInvoice()
    {
        // Arrange — create a template first
        var templateDto = CreateValidTemplateDto("From-Template Test");
        var template = await _templateService.CreateTemplateAsync(templateDto);

        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act
        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert
        invoice.ShouldNotBeNull();
        invoice.ClientId.ShouldBe(1);
        invoice.IssuerId.ShouldBe(2);
        invoice.Status.ShouldBe(EInvoiceStatus.Draft);
        invoice.InvoiceItem.Count.ShouldBe(1);
    }

    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_InvalidTemplateId_ShouldThrow()
    {
        // Arrange
        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act & Assert
        var act = () => _templateService.CreateInvoiceFromTemplateAsync(999, fromDto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("Template");
        ex.Message.ShouldContain("999");
        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_InactiveTemplate_ShouldThrow()
    {
        // Arrange — create template then deactivate it
        var templateDto = CreateValidTemplateDto("Inactive Template");
        var template = await _templateService.CreateTemplateAsync(templateDto);
        await _templateService.UpdateTemplateAsync(template.Id, new UpdateInvoiceTemplateDto { IsActive = false });

        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act & Assert
        var act = () => _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);
        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("not active");
    }

    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_ShouldIncrementUsageCount()
    {
        // Arrange
        var templateDto = CreateValidTemplateDto("Usage Counter Test");
        var template = await _templateService.CreateTemplateAsync(templateDto);
        template.UsageCount.ShouldBe(0);

        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act
        await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert — reload template to check updated usage stats
        var updated = await _templateService.GetTemplateByIdAsync(template.Id);
        updated!.UsageCount.ShouldBe(1);
        updated.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_ShouldCopyAllFields()
    {
        // Arrange — template with all optional fields set
        var templateDto = CreateValidTemplateDto("Full Fields");
        templateDto.ConstantSymbol = "0308";
        templateDto.SpecificSymbol = "12345";
        templateDto.BankAccountNumber = "123-456/0100";
        templateDto.IBAN = "CZ6508000000192000145399";
        templateDto.SWIFT = "GIBACZPX";
        templateDto.PaymentMethod = EPaymentMethod.BankTransfer;
        templateDto.Notes = "Monthly invoice notes";

        var template = await _templateService.CreateTemplateAsync(templateDto);
        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act
        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert — all template fields should be copied to the invoice
        invoice.ConstantSymbol.ShouldBe("0308");
        invoice.SpecificSymbol.ShouldBe("12345");
        invoice.BankAccountNumber.ShouldBe("123-456/0100");
        invoice.IBAN.ShouldBe("CZ6508000000192000145399");
        invoice.SWIFT.ShouldBe("GIBACZPX");
        invoice.PaymentMethod.ShouldBe(EPaymentMethod.BankTransfer);
        invoice.Notes.ShouldBe("Monthly invoice notes");
    }

    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_VariableSymbolOverride_ShouldUseOverride()
    {
        // Arrange
        var templateDto = CreateValidTemplateDto("VS Override");
        templateDto.VariableSymbol = "1234567890";
        var template = await _templateService.CreateTemplateAsync(templateDto);

        var fromDto = new CreateInvoiceFromTemplateDto
        {
            ClientId = 1,
            VariableSymbol = "9999999999" // Override template's value
        };

        // Act
        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert — override value should be used
        invoice.VariableSymbol.ShouldBe("9999999999");
    }

    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_NoVsOverride_ShouldNotInheritTemplateVs()
    {
        // Regression: previously template.VariableSymbol fell through into the new invoice,
        // which collided with the source invoice's VS (templates created via CreateTemplateFromInvoiceAsync
        // always carry the source invoice's VS). New invoice must derive its own unique VS from DocumentNumber.
        var templateDto = CreateValidTemplateDto("VS Inheritance Regression");
        templateDto.VariableSymbol = "2026005";
        var template = await _templateService.CreateTemplateAsync(templateDto);

        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        invoice.VariableSymbol.ShouldNotBe("2026005");
        invoice.VariableSymbol.ShouldNotBeNullOrEmpty();
    }

    // =====================================================================
    // CreateTemplateFromInvoiceAsync tests
    // =====================================================================

    [Fact]
    public async Task CreateTemplateFromInvoiceAsync_ShouldCreateTemplateFromInvoice()
    {
        // Arrange — first create an invoice
        var invoiceDto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = 1, IssuerId = 2, CurrencyId = 1,
            PaymentMethod = EPaymentMethod.Cash,
            Notes = "Source invoice notes",
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new() { OrderIndex = 1, Description = "Service", Quantity = 1, Unit = "hr", UnitPrice = 1000 }
            }
        };
        var invoice = await _invoiceService.CreateInvoiceAsync(invoiceDto);

        // Act
        var template = await _templateService.CreateTemplateFromInvoiceAsync(
            invoice.Id, "Template from Invoice", "Auto-generated");

        // Assert
        template.ShouldNotBeNull();
        template.Name.ShouldBe("Template from Invoice");
        template.Description.ShouldBe("Auto-generated");
        template.PaymentMethod.ShouldBe(EPaymentMethod.Cash);
        template.Notes.ShouldBe("Source invoice notes");
        template.InvoiceItem.Count.ShouldBe(1);
        template.InvoiceItem[0].Description.ShouldBe("Service");
    }

    // =====================================================================
    // Soft-delete (deactivation) tests
    // =====================================================================

    [Fact]
    public async Task DeleteTemplateAsync_ShouldDeactivate()
    {
        // Arrange
        var template = await _templateService.CreateTemplateAsync(CreateValidTemplateDto("To Delete"));

        // Act
        var deleted = await _templateService.DeleteTemplateAsync(template.Id);

        // Assert
        deleted.ShouldBeTrue();
        var loaded = await _templateService.GetTemplateByIdAsync(template.Id);
        loaded!.IsActive.ShouldBeFalse();
    }

    // =====================================================================
    // Integration test: CreateInvoiceFromTemplateAsync + prefix/suffix fix
    // =====================================================================

    /// <summary>
    /// Integration test covering the CreateInvoiceFromTemplateAsync code path
    /// (InvoiceTemplateService → InvoiceService → GenerateDocumentNumberAsync).
    ///
    /// Verifies that the two-phase fix (sequence vs prefix/suffix resolution)
    /// propagates correctly when an invoice is created via the template service,
    /// not just via the direct InvoiceService.CreateInvoiceAsync path.
    ///
    /// Scenario: template has a custom NumberSequenceId AND the client has a
    /// billing-settings prefix. Both must be honoured simultaneously in the result.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_TemplateSequence_WithClientPrefix_ShouldApplyBoth()
    {
        // Arrange — add BillingSettings with "EU-" prefix to client #1
        _context.Set<BillingSettings>().Add(new BillingSettings
        {
            ClientId = 1,
            InvoiceNumberPrefix = "EU-"
        });
        _context.SaveChanges();

        // Create a number sequence that the template will reference
        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 50,
            Name = "EU Export Sequence",
            DocumentType = EDocumentType.Invoice,
            CurrentNumber = 1,
            IsDefault = false,
            IsActive = true,
            NumberSequenceFormatId = 0 // Not validated in unit tests
        });
        _context.SaveChanges();

        // Configure the named-sequence mock to return a recognisable number
        _numberSequence
            .GenerateNextNumberAsync(50L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("EXP2026001");

        // Build template with the custom sequence
        var templateDto = CreateValidTemplateDto("EU Template");
        templateDto.NumberSequenceId = 50;
        var template = await _templateService.CreateTemplateAsync(templateDto);

        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act — goes through InvoiceTemplateService → InvoiceService
        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert — sequence from template AND prefix from client must both be present
        invoice.ShouldNotBeNull();
        invoice.DocumentNumber.ShouldBe("EU-EXP2026001");
        invoice.DocumentNumber.ShouldStartWith("EU-");
        invoice.DocumentNumber.ShouldContain("EXP2026001");
    }

    /// <summary>
    /// Issue #104 regression: template with NumberSequenceId + client with suffix.
    /// The suffix must be appended to the document number even when the sequence comes
    /// from the template (named sequence path in GenerateDocumentNumberAsync).
    /// </summary>
    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_TemplateSequence_WithClientSuffix_ShouldApplySuffix()
    {
        // Arrange — client has "-EXPORT" suffix in BillingSettings
        _context.Set<BillingSettings>().Add(new BillingSettings
        {
            ClientId = 1,
            InvoiceNumberSuffix = "-EXPORT"
        });
        _context.SaveChanges();

        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 51,
            Name = "Export Sequence",
            DocumentType = EDocumentType.Invoice,
            CurrentNumber = 1,
            IsDefault = false,
            IsActive = true,
            NumberSequenceFormatId = 0
        });
        _context.SaveChanges();

        _numberSequence
            .GenerateNextNumberAsync(51L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026100");

        var templateDto = CreateValidTemplateDto("Suffix Template");
        templateDto.NumberSequenceId = 51;
        var template = await _templateService.CreateTemplateAsync(templateDto);

        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act
        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert — bare number wrapped with client suffix
        invoice.DocumentNumber.ShouldBe("INV2026100-EXPORT");
        invoice.DocumentNumber.ShouldEndWith("-EXPORT");
    }

    /// <summary>
    /// Issue #104 regression: client with NO BillingSettings — template's own NumberSequenceId
    /// must be used as-is, without any prefix or suffix.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_NoBillingSettings_UsesTemplateSequenceAsIs()
    {
        // Arrange — client #1 has NO BillingSettings (nothing added)
        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 52,
            Name = "Plain Sequence",
            DocumentType = EDocumentType.Invoice,
            CurrentNumber = 1,
            IsDefault = false,
            IsActive = true,
            NumberSequenceFormatId = 0
        });
        _context.SaveChanges();

        _numberSequence
            .GenerateNextNumberAsync(52L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026999");

        var templateDto = CreateValidTemplateDto("Plain Template");
        templateDto.NumberSequenceId = 52;
        var template = await _templateService.CreateTemplateAsync(templateDto);

        var fromDto = new CreateInvoiceFromTemplateDto { ClientId = 1 };

        // Act
        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert — bare sequence number, no wrapping
        invoice.DocumentNumber.ShouldBe("INV2026999");
    }

    /// <summary>
    /// Issue #104 regression: CreditNote created from template, client has CreditNoteNumberPrefix.
    /// The prefix must appear in the credit note's document number.
    /// </summary>
    [Fact]
    public async Task CreateInvoiceFromTemplateAsync_CreditNoteTemplate_WithClientCreditNotePrefix_ShouldApplyPrefix()
    {
        // Arrange — seed a completed invoice to serve as the OriginalInvoice for the credit note
        var originalInvoice = new Invoice
        {
            DocumentType = EDocumentType.Invoice,
            Status = EInvoiceStatus.Completed,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            DocumentNumber = "ORIG-INV-001",
            VariableSymbol = "1000000001",
            IssueDate = DateTime.UtcNow.Date,
            TaxableSupplyDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.Date.AddDays(14),
            InvoiceItem = new List<InvoiceItem>()
        };
        _context.Invoice.Add(originalInvoice);
        await _context.SaveChangesAsync();

        // Client has a CreditNoteNumberPrefix "RET-"
        _context.Set<BillingSettings>().Add(new BillingSettings
        {
            ClientId = 1,
            CreditNoteNumberPrefix = "RET-"
        });
        _context.SaveChanges();

        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 53,
            Name = "CreditNote Sequence",
            DocumentType = EDocumentType.CreditNote,
            CurrentNumber = 1,
            IsDefault = false,
            IsActive = true,
            NumberSequenceFormatId = 0
        });
        _context.SaveChanges();

        // Credit-note sequence returns a number whose digits are unique in this DB
        _numberSequence
            .GenerateNextNumberAsync(53L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("CN20260077");

        // CreditNote template must reference the original invoice
        var templateDto = new CreateInvoiceTemplateDto
        {
            Name = "Credit Note Template",
            DocumentType = EDocumentType.CreditNote,
            IssuerId = 2,
            CurrencyId = 1,
            DueDateOffsetDays = 0,
            PaymentMethod = EPaymentMethod.BankTransfer,
            NumberSequenceId = 53,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Refund item",
                    Quantity = 1,
                    Unit = "pcs",
                    UnitPrice = 500
                }
            }
        };
        var template = await _templateService.CreateTemplateAsync(templateDto);

        var fromDto = new CreateInvoiceFromTemplateDto
        {
            ClientId = 1,
            // CreditNote requires OriginalInvoiceId
            OriginalInvoiceId = originalInvoice.Id
        };

        // Act
        var invoice = await _templateService.CreateInvoiceFromTemplateAsync(template.Id, fromDto);

        // Assert — credit note must have the RET- prefix from client's BillingSettings
        invoice.DocumentNumber.ShouldBe("RET-CN20260077");
        invoice.DocumentNumber.ShouldStartWith("RET-");
    }

    /// <summary>
    /// Issue #104 regression (direct creation, not from template): client with prefix
    /// in BillingSettings, invoice created directly via InvoiceService — prefix must apply.
    /// This guards the non-template path against future regressions.
    /// </summary>
    [Fact]
    public async Task DirectInvoiceCreation_ClientWithPrefix_ShouldApplyPrefix()
    {
        // Arrange — client #1 has "VIP-" prefix
        _context.Set<BillingSettings>().Add(new BillingSettings
        {
            ClientId = 1,
            InvoiceNumberPrefix = "VIP-"
        });
        _context.SaveChanges();

        _context.Set<NumberSequence>().Add(new NumberSequence
        {
            Id = 54,
            Name = "VIP Sequence",
            DocumentType = EDocumentType.Invoice,
            CurrentNumber = 1,
            IsDefault = false,
            IsActive = true,
            NumberSequenceFormatId = 0
        });
        _context.SaveChanges();

        _numberSequence
            .GenerateNextNumberAsync(54L, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns("INV2026055");

        var invoiceDto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = 1,
            IssuerId = 2,
            CurrencyId = 1,
            NumberSequenceId = 54,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new() { OrderIndex = 1, Description = "VIP service", Quantity = 1, Unit = "pcs", UnitPrice = 5000 }
            }
        };

        // Act — direct creation via InvoiceService (not via template)
        var invoice = await _invoiceService.CreateInvoiceAsync(invoiceDto);

        // Assert — prefix must be applied on the non-template path too
        invoice.DocumentNumber.ShouldBe("VIP-INV2026055");
        invoice.DocumentNumber.ShouldStartWith("VIP-");
    }

    // =====================================================================
    // GetTemplatesPagedAsync — column filters & sorting
    // =====================================================================

    /// <summary>
    /// Helper: creates one active and one inactive template with distinct names.
    /// Returns nothing — tests query via GetTemplatesPagedAsync.
    /// </summary>
    private async Task SeedTwoTemplatesForFiltering()
    {
        await _templateService.CreateTemplateAsync(CreateValidTemplateDto("Alpha Hosting"));
        var beta = await _templateService.CreateTemplateAsync(CreateValidTemplateDto("Beta Consulting"));

        // Deactivate the second template directly — no service method needed for the test setup
        var entity = await _context.Set<InvoiceTemplate>().FindAsync(beta.Id);
        entity!.IsActive = false;
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task GetTemplatesPagedAsync_NameFilter_ReturnsOnlyMatchingRows()
    {
        await SeedTwoTemplatesForFiltering();

        // Case-insensitive contains on Name only — must not match description/category
        var result = await _templateService.GetTemplatesPagedAsync(name: "beta");

        result.Items.Count.ShouldBe(1);
        result.Items[0].Name.ShouldBe("Beta Consulting");
    }

    [Fact]
    public async Task GetTemplatesPagedAsync_NameFilter_NoMatch_ReturnsEmpty()
    {
        await SeedTwoTemplatesForFiltering();

        var result = await _templateService.GetTemplatesPagedAsync(name: "does-not-exist");

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetTemplatesPagedAsync_SortByIsActive_Ascending_InactiveFirst()
    {
        await SeedTwoTemplatesForFiltering();

        // IsActive was previously missing from validSortFields — clicking the column
        // header silently fell back to Name sort. This guards the fix.
        var result = await _templateService.GetTemplatesPagedAsync(
            sortBy: "IsActive", isDescending: false);

        result.Items.Count.ShouldBe(2);
        result.Items[0].IsActive.ShouldBeFalse(); // false < true in ascending order
        result.Items[1].IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task GetTemplatesPagedAsync_UnknownSortField_FallsBackToNameWithoutError()
    {
        await SeedTwoTemplatesForFiltering();

        var result = await _templateService.GetTemplatesPagedAsync(sortBy: "nonsense-field");

        result.Items.Count.ShouldBe(2);
        result.Items[0].Name.ShouldBe("Alpha Hosting"); // fallback = Name ascending
    }
}
