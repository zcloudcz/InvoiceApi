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
/// Unit tests for issue #45 — InvoiceItem.ReverseChargeCodeId FK and PDP VAT calculation.
///
/// Tests cover (per AC):
/// - Pure Standard invoice: regression check (existing behaviour unchanged).
/// - Pure PDP invoice: TotalVat = 0, TotalAmount = Subtotal, InformationalVatAmount populated.
/// - Mixed Standard + PDP: TotalVat = only Standard items, PDP adds 0 to invoice VAT.
/// - PDP item missing ReverseChargeCodeId → validation error.
/// - Standard item with ReverseChargeCodeId set → validation error.
/// - Exempt and OutOfScope items: VatAmount = 0, InformationalVatAmount = 0.
/// </summary>
public class InvoiceServiceReverseChargeTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;

    // IDs for seed data
    private const long ClientId = 1;
    private const long NonVatIssuerId = 2;
    private const long VatIssuerId = 3;
    private const long CurrencyId = 1;
    private const long VatRate21Id = 1;
    private const long ReverseChargeCodeId = 10; // code "11" = construction work (§92d)

    public InvoiceServiceReverseChargeTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        var logger = Substitute.For<ILogger<InvoiceService>>();
        var numberSequence = Substitute.For<INumberSequenceService>();

        // All document-number generation returns a fixed string — not the concern of these tests.
        numberSequence
            .GenerateNextNumberForDocumentTypeAsync(
                Arg.Any<EDocumentType>(), Arg.Any<DateTime>(),
                Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns("INV-2026-001");

        _service = new InvoiceService(_context, numberSequence, Substitute.For<ITenantReadinessService>(), logger);

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
            Id = NonVatIssuerId, CompanyName = "Firma bez DPH", RegistrationNumber = "CZ22222222",
            IsIssuer = true, IsActive = true, IsVatPayer = false
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

        // Seed one reverse charge code (stavební práce §92d, code "11").
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

    /// <summary>Returns a CreateInvoiceDto header (no items) for a VAT-paying issuer.</summary>
    private CreateInvoiceDto BuildInvoiceHeader(long issuerId = VatIssuerId) => new()
    {
        DocumentType = EDocumentType.Invoice,
        ClientId = ClientId,
        IssuerId = issuerId,
        CurrencyId = CurrencyId
    };

    /// <summary>Creates a standard-regime item with 21% VAT, 1000 CZK base, via VatRate lookup.</summary>
    private CreateInvoiceItemDto StandardItem(int orderIndex = 1) => new()
    {
        OrderIndex = orderIndex,
        Description = "Konzultační služby",
        Quantity = 10,
        Unit = "hod",
        UnitPrice = 100,          // base = 10 * 100 = 1000
        VatRateId = VatRate21Id,
        VatRegime = EVatRegime.Standard
    };

    /// <summary>
    /// Creates a reverse-charge item with 21% VAT rate and the seeded ReverseChargeCodeId.
    /// The invoice will NOT bill VAT but will set InformationalVatAmount.
    /// </summary>
    private CreateInvoiceItemDto ReverseChargeItem(int orderIndex = 2) => new()
    {
        OrderIndex = orderIndex,
        Description = "Montážní práce §92d",
        Quantity = 5,
        Unit = "hod",
        UnitPrice = 200,           // base = 5 * 200 = 1000
        VatRateId = VatRate21Id,
        VatRegime = EVatRegime.ReverseCharge,
        ReverseChargeCodeId = ReverseChargeCodeId
    };

    // ─── Standard-only invoice (regression) ─────────────────────────────────

    /// <summary>
    /// A Standard-only invoice must behave exactly as before: VAT is charged,
    /// TotalWithVat = TotalBeforeVat + VatAmount, InformationalVatAmount = 0.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_StandardOnly_VatCalculatedNormally()
    {
        // Arrange — 1 000 CZK base × 21% = 210 CZK VAT
        var dto = BuildInvoiceHeader();
        dto.InvoiceItem = new List<CreateInvoiceItemDto> { StandardItem() };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        result.TotalBeforeVat.ShouldBe(1000m);
        result.TotalVat.ShouldBe(210m);
        result.TotalWithVat.ShouldBe(1210m);

        var item = result.InvoiceItem.Single();
        item.VatAmount.ShouldBe(210m);
        item.TotalWithVat.ShouldBe(1210m);
        item.InformationalVatAmount.ShouldBe(0m);
        item.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    // ─── PDP-only invoice ────────────────────────────────────────────────────

    /// <summary>
    /// A PDP-only invoice: no VAT is billed (VatAmount = 0), TotalAmount equals subtotal,
    /// InformationalVatAmount shows what the buyer must self-assess (§92a ZDPH).
    /// </summary>
    [Fact]
    public async Task CreateInvoice_ReverseChargeOnly_TotalVatIsZero()
    {
        // Arrange — 1 000 CZK base, PDP → billed VAT = 0
        var dto = BuildInvoiceHeader();
        dto.InvoiceItem = new List<CreateInvoiceItemDto> { ReverseChargeItem(orderIndex: 1) };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        result.TotalBeforeVat.ShouldBe(1000m);
        result.TotalVat.ShouldBe(0m);             // VAT not billed
        result.TotalWithVat.ShouldBe(1000m);       // client pays only the base

        var item = result.InvoiceItem.Single();
        item.VatAmount.ShouldBe(0m);
        item.TotalWithVat.ShouldBe(1000m);
        // Informational: 1 000 × 21% = 210 CZK (buyer must self-assess this amount)
        item.InformationalVatAmount.ShouldBe(210m);
        item.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
        item.ReverseChargeCodeId.ShouldBe(ReverseChargeCodeId);
    }

    // ─── Mixed Standard + PDP invoice ────────────────────────────────────────

    /// <summary>
    /// Mixed invoice: Standard item contributes VAT to the invoice total;
    /// ReverseCharge item contributes 0 VAT — only InformationalVatAmount is set.
    /// TotalVat = Standard VAT only.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_MixedStandardAndReverseCharge_TotalVatExcludesPdp()
    {
        // Arrange — Standard: 1000 base, 210 VAT; PDP: 1000 base, 0 billed VAT, 210 informational
        var dto = BuildInvoiceHeader();
        dto.InvoiceItem = new List<CreateInvoiceItemDto>
        {
            StandardItem(orderIndex: 1),
            ReverseChargeItem(orderIndex: 2)
        };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert invoice totals
        result.TotalBeforeVat.ShouldBe(2000m);  // 1000 + 1000
        result.TotalVat.ShouldBe(210m);          // only Standard item VAT
        result.TotalWithVat.ShouldBe(2210m);     // 2000 base + 210 Standard VAT

        // Standard item
        var stdItem = result.InvoiceItem.Single(i => i.VatRegime == EVatRegime.Standard);
        stdItem.VatAmount.ShouldBe(210m);
        stdItem.InformationalVatAmount.ShouldBe(0m);

        // PDP item
        var pdpItem = result.InvoiceItem.Single(i => i.VatRegime == EVatRegime.ReverseCharge);
        pdpItem.VatAmount.ShouldBe(0m);
        pdpItem.InformationalVatAmount.ShouldBe(210m);
    }

    // ─── Validation: PDP item missing ReverseChargeCodeId ────────────────────

    /// <summary>
    /// A ReverseCharge item without ReverseChargeCodeId must be rejected.
    /// The code is mandatory for EPO XML reporting (A.1/B.1 sections).
    /// </summary>
    [Fact]
    public async Task CreateInvoice_ReverseChargeItemWithoutCode_ThrowsValidationError()
    {
        // Arrange — PDP item but no code
        var dto = BuildInvoiceHeader();
        dto.InvoiceItem = new List<CreateInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Stavba bez kódu",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = 500,
                VatRateId = VatRate21Id,
                VatRegime = EVatRegime.ReverseCharge,
                ReverseChargeCodeId = null           // ← missing
            }
        };

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateInvoiceAsync(dto));

        // Assert
        ex.Message.ShouldContain("ReverseChargeCodeId");
        ex.Message.ShouldContain("ReverseCharge");
    }

    // ─── Validation: Standard item with ReverseChargeCodeId set ──────────────

    /// <summary>
    /// A Standard item with ReverseChargeCodeId set must be rejected.
    /// Mixing regime and code would produce incorrect EPO reporting.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_StandardItemWithReverseChargeCode_ThrowsValidationError()
    {
        // Arrange — Standard item but with a PDP code set
        var dto = BuildInvoiceHeader();
        dto.InvoiceItem = new List<CreateInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Konzultace s nesprávným kódem",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = 1000,
                VatRateId = VatRate21Id,
                VatRegime = EVatRegime.Standard,
                ReverseChargeCodeId = ReverseChargeCodeId  // ← wrong
            }
        };

        // Act
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.CreateInvoiceAsync(dto));

        // Assert
        ex.Message.ShouldContain("ReverseChargeCodeId");
        ex.Message.ShouldContain("Standard");
    }

    // ─── Exempt and OutOfScope items ─────────────────────────────────────────

    /// <summary>
    /// Exempt items: VatAmount = 0, InformationalVatAmount = 0 (not ReverseCharge).
    /// TotalWithVat = TotalBeforeVat.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_ExemptItem_VatAndInformationalAreZero()
    {
        // Arrange — non-VAT payer issuer so we can omit VatRateId for Exempt items
        var dto = BuildInvoiceHeader(issuerId: NonVatIssuerId);
        dto.InvoiceItem = new List<CreateInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Finančně osvobozená služba",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = 2000,
                VatRegime = EVatRegime.Exempt
                // No VatRateId, no ReverseChargeCodeId — both must be absent for Exempt
            }
        };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        result.TotalBeforeVat.ShouldBe(2000m);
        result.TotalVat.ShouldBe(0m);
        result.TotalWithVat.ShouldBe(2000m);

        var item = result.InvoiceItem.Single();
        item.VatAmount.ShouldBe(0m);
        item.InformationalVatAmount.ShouldBe(0m);
        item.VatRegime.ShouldBe(EVatRegime.Exempt);
    }

    /// <summary>
    /// OutOfScope items: same as Exempt — no VAT at all, no informational amount.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_OutOfScopeItem_VatAndInformationalAreZero()
    {
        // Arrange — non-VAT payer issuer
        var dto = BuildInvoiceHeader(issuerId: NonVatIssuerId);
        dto.InvoiceItem = new List<CreateInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Interní přefakturace mimo rozsah DPH",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = 500,
                VatRegime = EVatRegime.OutOfScope
            }
        };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        result.TotalVat.ShouldBe(0m);
        result.TotalWithVat.ShouldBe(500m);

        var item = result.InvoiceItem.Single();
        item.VatAmount.ShouldBe(0m);
        item.InformationalVatAmount.ShouldBe(0m);
        item.VatRegime.ShouldBe(EVatRegime.OutOfScope);
    }

    // ─── ReverseChargeCodeId persisted on entity ──────────────────────────────

    /// <summary>
    /// ReverseChargeCodeId is persisted to the database and returned via GetInvoiceByIdAsync.
    /// This ensures the FK is stored correctly and available for PDF/ISDOC export.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_ReverseChargeItem_ReverseChargeCodeIdPersistedToDb()
    {
        // Arrange
        var dto = BuildInvoiceHeader();
        dto.InvoiceItem = new List<CreateInvoiceItemDto> { ReverseChargeItem(orderIndex: 1) };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — ReverseChargeCodeId must round-trip through DB
        result.InvoiceItem.Single().ReverseChargeCodeId.ShouldBe(ReverseChargeCodeId);

        // Verify at the EF level directly
        var dbItem = await _context.InvoiceItem.FindAsync((long)result.InvoiceItem.Single().Id);
        dbItem.ShouldNotBeNull();
        dbItem.ReverseChargeCodeId.ShouldBe(ReverseChargeCodeId);
        dbItem.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
    }

    // ─── Default VatRegime = Standard (backward compatibility) ───────────────

    /// <summary>
    /// When VatRegime is not set in the DTO, it defaults to Standard.
    /// Existing invoice creation code that doesn't set VatRegime must not break.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_ItemWithoutVatRegime_DefaultsToStandard()
    {
        // Arrange — DTO item does not set VatRegime (mimics old callers)
        var dto = BuildInvoiceHeader();
        dto.InvoiceItem = new List<CreateInvoiceItemDto>
        {
            new()
            {
                OrderIndex = 1,
                Description = "Starý kód bez VatRegime",
                Quantity = 1,
                Unit = "pcs",
                UnitPrice = 800,
                VatRateId = VatRate21Id
                // VatRegime not set — C# default for enum is 0 = Standard
            }
        };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert
        var item = result.InvoiceItem.Single();
        item.VatRegime.ShouldBe(EVatRegime.Standard);
        item.VatAmount.ShouldBe(168m);           // 800 × 21% = 168
        item.InformationalVatAmount.ShouldBe(0m);
    }

    // ─── Non-VAT-payer issuer + ReverseCharge item ────────────────────────────

    /// <summary>
    /// A non-VAT-payer issuer can never use reverse charge (PDP is a VAT-payer-only regime —
    /// there is no VAT for the buyer to self-assess). <see cref="NonVatPayerItems.StripVat"/>
    /// silently downgrades the item to Standard and clears the code, the same way it already
    /// strips VatRateId/VatRatePercentage for every other item — one rule, not a separate
    /// rejection path. The UI must additionally never offer the PDP toggle to a non-VAT-payer
    /// issuer (see InvoiceDetail.razor), but the backend guard is this test's subject.
    /// </summary>
    [Fact]
    public async Task CreateInvoice_NonVatPayerIssuer_ReverseChargeItemDowngradedToStandard()
    {
        // Arrange — non-VAT-payer issuer, item requests ReverseCharge with a valid code
        var dto = BuildInvoiceHeader(issuerId: NonVatIssuerId);
        dto.InvoiceItem = new List<CreateInvoiceItemDto> { ReverseChargeItem(orderIndex: 1) };

        // Act
        var result = await _service.CreateInvoiceAsync(dto);

        // Assert — regime downgraded, code cleared, no VAT anywhere (same as every other item
        // on a non-VAT-payer invoice)
        var item = result.InvoiceItem.Single();
        item.VatRegime.ShouldBe(EVatRegime.Standard);
        item.ReverseChargeCodeId.ShouldBeNull();
        item.VatAmount.ShouldBe(0m);
        item.InformationalVatAmount.ShouldBe(0m);
        result.TotalVat.ShouldBe(0m);
    }
}
