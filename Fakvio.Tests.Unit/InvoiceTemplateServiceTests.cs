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

        _invoiceService = new InvoiceService(_context, _numberSequence, _invoiceLogger);
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
}
