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
/// Unit tests for UpdateInvoiceAsync with the VatRegime / ReverseChargeCodeId / InformationalVatAmount
/// fields introduced in issue #45.
///
/// The update path re-uses ValidateReverseChargeCodes() and CalculateItemVat() internally.
/// These tests confirm that the same business rules enforced at creation time are also enforced
/// when an existing invoice is updated with new items.
/// </summary>
public class InvoiceServiceReverseChargeUpdateTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;

    private const long ClientId = 1;
    private const long VatIssuerId = 2;
    private const long CurrencyId = 1;
    private const long VatRate21Id = 1;
    private const long ReverseChargeCodeId = 10;

    public InvoiceServiceReverseChargeUpdateTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        var logger = Substitute.For<ILogger<InvoiceService>>();
        var numberSequence = Substitute.For<INumberSequenceService>();

        numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV-2026-001");

        _service = new InvoiceService(_context, numberSequence, logger);

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── Seed ────────────────────────────────────────────────────────────────

    private void SeedTestData()
    {
        _context.Client.Add(new Client
        {
            Id = ClientId, CompanyName = "Odběratel s.r.o.", RegistrationNumber = "CZ11111111",
            IsIssuer = false, IsActive = true
        });

        _context.Client.Add(new Client
        {
            Id = VatIssuerId, CompanyName = "Plátce DPH s.r.o.", RegistrationNumber = "CZ33333333",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        });

        _context.Currency.Add(new Currency
        {
            Id = CurrencyId, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });

        _context.VatRate.Add(new VatRate
        {
            Id = VatRate21Id, Name = "Základní sazba 21%", Rate = 21,
            IsDefault = true, ValidFrom = DateTime.UtcNow.AddYears(-5), IsActive = true
        });

        _context.ReverseChargeCode.Add(new ReverseChargeCode
        {
            Id = ReverseChargeCodeId, Code = "11",
            NameCs = "Stavební nebo montážní práce",
            NameEn = "Construction or assembly work",
            ParagraphRef = "§92d",
            ValidFrom = new DateOnly(2016, 1, 1),
            IsActive = true
        });

        _context.SaveChanges();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Creates a draft invoice (Standard item, 1000 base) and returns its ID.</summary>
    private async Task<long> CreateDraftInvoiceAsync()
    {
        var createDto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = ClientId,
            IssuerId = VatIssuerId,
            CurrencyId = CurrencyId,
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Initial item",
                    Quantity = 10, Unit = "hod", UnitPrice = 100,
                    VatRateId = VatRate21Id,
                    VatRegime = EVatRegime.Standard
                }
            }
        };

        var result = await _service.CreateInvoiceAsync(createDto);
        return result.Id;
    }

    private CreateInvoiceItemDto StandardItem(int orderIndex = 1) => new()
    {
        OrderIndex = orderIndex,
        Description = "Konzultační služby",
        Quantity = 10, Unit = "hod", UnitPrice = 100,
        VatRateId = VatRate21Id,
        VatRegime = EVatRegime.Standard
    };

    private CreateInvoiceItemDto ReverseChargeItem(int orderIndex = 1) => new()
    {
        OrderIndex = orderIndex,
        Description = "Montážní práce §92d",
        Quantity = 5, Unit = "hod", UnitPrice = 200,
        VatRateId = VatRate21Id,
        VatRegime = EVatRegime.ReverseCharge,
        ReverseChargeCodeId = ReverseChargeCodeId
    };

    // ─── Update: ReverseCharge item — totals correct ─────────────────────────

    /// <summary>
    /// Updating an invoice to replace its item with a ReverseCharge item:
    /// TotalVat must become 0 and InformationalVatAmount must be set.
    /// </summary>
    [Fact]
    public async Task UpdateInvoice_ReplaceWithReverseChargeItem_TotalVatBecomesZero()
    {
        // Arrange
        var invoiceId = await CreateDraftInvoiceAsync();

        var updateDto = new UpdateInvoiceDto
        {
            InvoiceItem = new List<CreateInvoiceItemDto> { ReverseChargeItem() }
        };

        // Act
        var result = await _service.UpdateInvoiceAsync(invoiceId, updateDto);

        // Assert invoice totals
        result.ShouldNotBeNull();
        result.TotalBeforeVat.ShouldBe(1000m);
        result.TotalVat.ShouldBe(0m);
        result.TotalWithVat.ShouldBe(1000m);

        // Assert item-level fields
        var item = result.InvoiceItem.Single();
        item.VatAmount.ShouldBe(0m);
        item.InformationalVatAmount.ShouldBe(210m);   // 1000 × 21%
        item.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
        item.ReverseChargeCodeId.ShouldBe(ReverseChargeCodeId);
    }

    // ─── Update: Standard item — regression ──────────────────────────────────

    /// <summary>
    /// Updating an invoice with a plain Standard item must not alter VAT behaviour —
    /// regression check that update path still computes VAT normally.
    /// </summary>
    [Fact]
    public async Task UpdateInvoice_ReplaceWithStandardItem_VatCalculatedNormally()
    {
        // Arrange — start with a ReverseCharge item, swap to Standard
        var createDto = new CreateInvoiceDto
        {
            DocumentType = EDocumentType.Invoice,
            ClientId = ClientId, IssuerId = VatIssuerId, CurrencyId = CurrencyId,
            InvoiceItem = new List<CreateInvoiceItemDto> { ReverseChargeItem() }
        };
        var invoice = await _service.CreateInvoiceAsync(createDto);

        var updateDto = new UpdateInvoiceDto
        {
            InvoiceItem = new List<CreateInvoiceItemDto> { StandardItem() }
        };

        // Act
        var result = await _service.UpdateInvoiceAsync(invoice.Id, updateDto);

        // Assert — now Standard: VAT is charged, informational = 0
        result.ShouldNotBeNull();
        result.TotalVat.ShouldBe(210m);

        var item = result.InvoiceItem.Single();
        item.VatAmount.ShouldBe(210m);
        item.InformationalVatAmount.ShouldBe(0m);
        item.VatRegime.ShouldBe(EVatRegime.Standard);
        item.ReverseChargeCodeId.ShouldBeNull();
    }

    // ─── Update: Mixed items — totals ────────────────────────────────────────

    /// <summary>
    /// Updating an invoice to have both Standard and ReverseCharge items:
    /// TotalVat = Standard VAT only; ReverseCharge contributes only InformationalVatAmount.
    /// </summary>
    [Fact]
    public async Task UpdateInvoice_MixedItems_TotalVatExcludesPdpItems()
    {
        // Arrange
        var invoiceId = await CreateDraftInvoiceAsync();

        var updateDto = new UpdateInvoiceDto
        {
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                StandardItem(orderIndex: 1),
                ReverseChargeItem(orderIndex: 2)
            }
        };

        // Act
        var result = await _service.UpdateInvoiceAsync(invoiceId, updateDto);

        // Assert — only Standard item contributes billed VAT
        result.ShouldNotBeNull();
        result.TotalBeforeVat.ShouldBe(2000m);
        result.TotalVat.ShouldBe(210m);    // Standard item 1000 × 21%
        result.TotalWithVat.ShouldBe(2210m);

        var stdItem = result.InvoiceItem.Single(i => i.VatRegime == EVatRegime.Standard);
        stdItem.VatAmount.ShouldBe(210m);
        stdItem.InformationalVatAmount.ShouldBe(0m);

        var pdpItem = result.InvoiceItem.Single(i => i.VatRegime == EVatRegime.ReverseCharge);
        pdpItem.VatAmount.ShouldBe(0m);
        pdpItem.InformationalVatAmount.ShouldBe(210m);
    }

    // ─── Update: Validation — PDP without code ───────────────────────────────

    /// <summary>
    /// Updating an invoice with a ReverseCharge item that has no ReverseChargeCodeId
    /// must throw — same rule as on creation.
    /// </summary>
    [Fact]
    public async Task UpdateInvoice_ReverseChargeItemWithoutCode_ThrowsValidationError()
    {
        // Arrange
        var invoiceId = await CreateDraftInvoiceAsync();

        var updateDto = new UpdateInvoiceDto
        {
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "PDP bez kódu",
                    Quantity = 1, Unit = "pcs", UnitPrice = 500,
                    VatRateId = VatRate21Id,
                    VatRegime = EVatRegime.ReverseCharge,
                    ReverseChargeCodeId = null    // ← missing
                }
            }
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UpdateInvoiceAsync(invoiceId, updateDto));

        ex.Message.ShouldContain("ReverseChargeCodeId");
        ex.Message.ShouldContain("ReverseCharge");
    }

    // ─── Update: Validation — Standard item with PDP code ────────────────────

    /// <summary>
    /// Updating an invoice with a Standard item that has ReverseChargeCodeId set
    /// must throw — same rule as on creation.
    /// </summary>
    [Fact]
    public async Task UpdateInvoice_StandardItemWithReverseChargeCode_ThrowsValidationError()
    {
        // Arrange
        var invoiceId = await CreateDraftInvoiceAsync();

        var updateDto = new UpdateInvoiceDto
        {
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Standard s PDP kódem",
                    Quantity = 1, Unit = "pcs", UnitPrice = 1000,
                    VatRateId = VatRate21Id,
                    VatRegime = EVatRegime.Standard,
                    ReverseChargeCodeId = ReverseChargeCodeId   // ← wrong
                }
            }
        };

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.UpdateInvoiceAsync(invoiceId, updateDto));

        ex.Message.ShouldContain("ReverseChargeCodeId");
        ex.Message.ShouldContain("Standard");
    }

    // ─── Update: ReverseChargeCodeId persisted ────────────────────────────────

    /// <summary>
    /// After updating to a ReverseCharge item, ReverseChargeCodeId and VatRegime
    /// must be persisted to the database (round-trip check).
    /// </summary>
    [Fact]
    public async Task UpdateInvoice_ReverseChargeItem_FieldsPersistedToDb()
    {
        // Arrange
        var invoiceId = await CreateDraftInvoiceAsync();

        var updateDto = new UpdateInvoiceDto
        {
            InvoiceItem = new List<CreateInvoiceItemDto> { ReverseChargeItem() }
        };

        // Act
        var result = await _service.UpdateInvoiceAsync(invoiceId, updateDto);

        // Assert — verify DTO
        result.ShouldNotBeNull();
        var dtoItem = result.InvoiceItem.Single();
        dtoItem.ReverseChargeCodeId.ShouldBe(ReverseChargeCodeId);
        dtoItem.VatRegime.ShouldBe(EVatRegime.ReverseCharge);

        // Assert — verify at EF layer directly
        var dbItem = await _context.InvoiceItem.FindAsync((long)dtoItem.Id);
        dbItem.ShouldNotBeNull();
        dbItem.ReverseChargeCodeId.ShouldBe(ReverseChargeCodeId);
        dbItem.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
        dbItem.InformationalVatAmount.ShouldBe(210m);
    }

    // ─── CalculateItemVat: negative TotalBeforeVat on ReverseCharge ───────────

    /// <summary>
    /// A deduction row (negative UnitPrice) on a ReverseCharge item must produce
    /// a negative InformationalVatAmount and zero VatAmount.
    /// This guards the CalculateItemVat helper against sign inversion.
    /// </summary>
    [Fact]
    public async Task UpdateInvoice_NegativeReverseChargeItem_InformationalVatAmountIsNegative()
    {
        // Arrange — create a draft invoice with a regular Standard item first
        var invoiceId = await CreateDraftInvoiceAsync();

        // Update with a negative (deduction-like) ReverseCharge item — 1 × (-500) = -500 base
        var updateDto = new UpdateInvoiceDto
        {
            InvoiceItem = new List<CreateInvoiceItemDto>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Záporná PDP položka",
                    Quantity = 1, Unit = "pcs", UnitPrice = -500,
                    VatRateId = VatRate21Id,
                    VatRegime = EVatRegime.ReverseCharge,
                    ReverseChargeCodeId = ReverseChargeCodeId
                }
            }
        };

        // Act
        var result = await _service.UpdateInvoiceAsync(invoiceId, updateDto);

        // Assert
        result.ShouldNotBeNull();
        result.TotalBeforeVat.ShouldBe(-500m);
        result.TotalVat.ShouldBe(0m);
        result.TotalWithVat.ShouldBe(-500m);

        var item = result.InvoiceItem.Single();
        item.VatAmount.ShouldBe(0m);
        // Informational: -500 × 21% = -105 (negative — mirrors the base sign)
        item.InformationalVatAmount.ShouldBe(-105m);
    }
}
