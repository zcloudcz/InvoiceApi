// ============================================================================
// InvoiceServiceNestedReverseChargeCodeTests — coverage for issue #46.
//
// Issue #46 adds a read-only nested ReverseChargeCodeDto to every InvoiceItemDto.
// ZMapper only copies scalar properties, so InvoiceService.MapToDto populates that
// nested object by hand from the eagerly-loaded navigation property.
//
// That hand-written pairing is the riskiest code in the change: if it ever attaches
// a code to the wrong line, the invoice silently reports the wrong predmet plneni
// in the VAT control statement (kontrolni hlaseni). These tests pin the behaviour at
// the service level — the ZMapper-only tests in InvoiceItemMappingTests cannot, because
// ZMapper never touches the navigation property at all.
// ============================================================================

using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Invoice;
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
/// Verifies that InvoiceService read paths populate InvoiceItemDto.ReverseChargeCode
/// and that every line item receives the code belonging to its own row — not the code
/// of a neighbouring line.
/// </summary>
public class InvoiceServiceNestedReverseChargeCodeTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;

    // Seed identifiers. Kept as constants so assertions read as intent, not as magic numbers.
    private const long ClientId = 1;
    private const long IssuerId = 2;
    private const long CurrencyId = 1;
    private const long VatRateId = 1;

    // Two distinct MFCR codes — a single code cannot expose a mis-pairing bug,
    // because every line would look correct no matter which row it was taken from.
    private const long ConstructionCodeId = 10;   // "11" / §92d
    private const long GoldCodeId = 20;           // "1"  / §92b

    // Invoice ids used by the individual scenarios.
    private const long MixedInvoiceId = 100;
    private const long SameOrderIndexInvoiceId = 200;
    private const long StandardOnlyInvoiceId = 300;

    // Line item ids. Ids and OrderIndex values are deliberately inverted on the mixed
    // invoice so that "position in the list" and "primary key" never coincide.
    private const long MixedItemGoldId = 1001;         // OrderIndex 3
    private const long MixedItemStandardId = 1002;     // OrderIndex 2
    private const long MixedItemConstructionId = 1003; // OrderIndex 1

    private const long SharedIndexItemGoldId = 2001;
    private const long SharedIndexItemConstructionId = 2002;

    public InvoiceServiceNestedReverseChargeCodeTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        _service = new InvoiceService(
            _context,
            Substitute.For<INumberSequenceService>(),
            Substitute.For<ILogger<InvoiceService>>());

        SeedTestData();
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Seed ---------------------------------------------------------------

    private void SeedTestData()
    {
        _context.Client.Add(new Client
        {
            Id = ClientId,
            CompanyName = "Odběratel s.r.o.",
            RegistrationNumber = "CZ11111111",
            IsIssuer = false,
            IsActive = true
        });
        _context.Client.Add(new Client
        {
            Id = IssuerId,
            CompanyName = "Dodavatel a.s.",
            RegistrationNumber = "CZ22222222",
            IsIssuer = true,
            IsActive = true,
            IsVatPayer = true
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = CurrencyId,
            Code = "CZK",
            Name = "Czech Koruna",
            Symbol = "Kč",
            DecimalPlaces = 2,
            SortOrder = 1,
            IsActive = true
        });
        _context.VatRate.Add(new VatRate
        {
            Id = VatRateId,
            Name = "Základní sazba 21%",
            Rate = 21,
            IsDefault = true,
            ValidFrom = DateTime.UtcNow.AddYears(-5),
            IsActive = true
        });
        _context.ReverseChargeCode.Add(new ReverseChargeCode
        {
            Id = ConstructionCodeId,
            Code = "11",
            NameCs = "Stavební nebo montážní práce",
            NameEn = "Construction or assembly work",
            ParagraphRef = "§92d",
            ValidFrom = new DateOnly(2016, 1, 1),
            IsActive = true
        });
        _context.ReverseChargeCode.Add(new ReverseChargeCode
        {
            Id = GoldCodeId,
            Code = "1",
            NameCs = "Zlato",
            NameEn = "Gold",
            ParagraphRef = "§92b",
            ValidFrom = new DateOnly(2016, 1, 1),
            IsActive = true
        });
        _context.SaveChanges();

        // Mixed invoice: three lines, two different PDP codes, one Standard line in between.
        // Id order (1001, 1002, 1003) is the reverse of OrderIndex order (3, 2, 1), so the
        // query ordering by OrderIndex hands the lines back in an order that does not match
        // their primary keys.
        _context.Invoice.Add(new Invoice
        {
            Id = MixedInvoiceId,
            DocumentNumber = "INV-MIXED-046",
            ClientId = ClientId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            Status = EInvoiceStatus.Draft,
            DocumentType = EDocumentType.Invoice,
            IssueDate = DateTime.UtcNow,
            InvoiceItem =
            [
                BuildItem(MixedItemGoldId, orderIndex: 3, "Zlato §92b", EVatRegime.ReverseCharge, GoldCodeId),
                BuildItem(MixedItemStandardId, orderIndex: 2, "Konzultace", EVatRegime.Standard, null),
                BuildItem(MixedItemConstructionId, orderIndex: 1, "Stavební práce §92d", EVatRegime.ReverseCharge, ConstructionCodeId)
            ]
        });

        // Desynchronisation scenario: both lines share the same OrderIndex. OrderIndex carries
        // no uniqueness constraint, so ordering by it establishes no total order over these two
        // rows and their materialisation order is not guaranteed. Pairing by primary key is
        // what makes the outcome deterministic anyway.
        _context.Invoice.Add(new Invoice
        {
            Id = SameOrderIndexInvoiceId,
            DocumentNumber = "INV-SAMEIDX-046",
            ClientId = ClientId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            Status = EInvoiceStatus.Draft,
            DocumentType = EDocumentType.Invoice,
            IssueDate = DateTime.UtcNow,
            InvoiceItem =
            [
                BuildItem(SharedIndexItemGoldId, orderIndex: 1, "Zlato §92b", EVatRegime.ReverseCharge, GoldCodeId),
                BuildItem(SharedIndexItemConstructionId, orderIndex: 1, "Stavební práce §92d", EVatRegime.ReverseCharge, ConstructionCodeId)
            ]
        });

        // Control invoice with no PDP line at all — exercises the branch that skips the
        // nested-code mapping entirely.
        _context.Invoice.Add(new Invoice
        {
            Id = StandardOnlyInvoiceId,
            DocumentNumber = "INV-STDONLY-046",
            ClientId = ClientId,
            IssuerId = IssuerId,
            CurrencyId = CurrencyId,
            Status = EInvoiceStatus.Draft,
            DocumentType = EDocumentType.Invoice,
            IssueDate = DateTime.UtcNow,
            InvoiceItem =
            [
                BuildItem(3001, orderIndex: 1, "Konzultace", EVatRegime.Standard, null),
                BuildItem(3002, orderIndex: 2, "Školení", EVatRegime.Standard, null)
            ]
        });

        _context.SaveChanges();
    }

    /// <summary>
    /// Builds a line item. Amounts are irrelevant to these tests, so they stay uniform;
    /// only regime, code FK and ordering matter here.
    /// </summary>
    private static InvoiceItem BuildItem(long id, int orderIndex, string description, EVatRegime regime, long? codeId) => new()
    {
        Id = id,
        OrderIndex = orderIndex,
        Description = description,
        Quantity = 1,
        Unit = "ks",
        UnitPrice = 1000,
        VatRateId = VatRateId,
        VatRatePercentage = 21,
        TotalBeforeVat = 1000,
        VatAmount = regime == EVatRegime.ReverseCharge ? 0 : 210,
        TotalWithVat = regime == EVatRegime.ReverseCharge ? 1000 : 1210,
        VatRegime = regime,
        ReverseChargeCodeId = codeId,
        InformationalVatAmount = regime == EVatRegime.ReverseCharge ? 210 : 0
    };

    // --- The core guarantee: the code belongs to its own line ----------------

    /// <summary>
    /// Three lines, two different codes, Id order inverted against OrderIndex order.
    /// Every returned line must carry the nested code matching its own ReverseChargeCodeId.
    /// This is the assertion a mis-paired mapping cannot satisfy.
    /// </summary>
    [Fact]
    public async Task GetInvoiceByIdAsync_MixedRegimesAndTwoCodes_EachItemCarriesItsOwnCode()
    {
        var dto = await _service.GetInvoiceByIdAsync(MixedInvoiceId);

        dto.ShouldNotBeNull();
        dto.InvoiceItem.Count.ShouldBe(3);

        var construction = dto.InvoiceItem.Single(i => i.Id == MixedItemConstructionId);
        construction.ReverseChargeCodeId.ShouldBe(ConstructionCodeId);
        construction.ReverseChargeCode.ShouldNotBeNull();
        construction.ReverseChargeCode.Id.ShouldBe(ConstructionCodeId);
        construction.ReverseChargeCode.Code.ShouldBe("11");
        construction.ReverseChargeCode.ParagraphRef.ShouldBe("§92d");
        construction.ReverseChargeCode.NameCs.ShouldBe("Stavební nebo montážní práce");

        var gold = dto.InvoiceItem.Single(i => i.Id == MixedItemGoldId);
        gold.ReverseChargeCodeId.ShouldBe(GoldCodeId);
        gold.ReverseChargeCode.ShouldNotBeNull();
        gold.ReverseChargeCode.Id.ShouldBe(GoldCodeId);
        gold.ReverseChargeCode.Code.ShouldBe("1");
        gold.ReverseChargeCode.ParagraphRef.ShouldBe("§92b");

        // The Standard line sits between the two PDP lines — it must stay empty.
        var standard = dto.InvoiceItem.Single(i => i.Id == MixedItemStandardId);
        standard.ReverseChargeCodeId.ShouldBeNull();
        standard.ReverseChargeCode.ShouldBeNull();
    }

    /// <summary>
    /// The same invariant stated as a rule rather than per item: whenever the nested object is
    /// present it describes exactly the FK stored on that same line. Any pairing bug breaks
    /// this regardless of how the two collections happen to be ordered.
    /// </summary>
    [Fact]
    public async Task GetInvoiceByIdAsync_NestedCodeIdAlwaysMatchesItsOwnForeignKey()
    {
        var dto = await _service.GetInvoiceByIdAsync(MixedInvoiceId);

        dto.ShouldNotBeNull();
        foreach (var item in dto.InvoiceItem)
        {
            if (item.ReverseChargeCode is null)
            {
                continue;
            }

            item.ReverseChargeCodeId.ShouldBe(
                item.ReverseChargeCode.Id,
                $"line {item.Id} carries a nested code that does not match its own FK");
        }
    }

    /// <summary>
    /// Desynchronisation case: two PDP lines share one OrderIndex, so nothing orders them
    /// relative to each other. Pairing by Id keeps the result deterministic; pairing by list
    /// position would depend on materialisation order and could swap the two codes.
    /// </summary>
    [Fact]
    public async Task GetInvoiceByIdAsync_ItemsShareOrderIndex_CodesStillPairedByItemId()
    {
        var dto = await _service.GetInvoiceByIdAsync(SameOrderIndexInvoiceId);

        dto.ShouldNotBeNull();
        dto.InvoiceItem.Count.ShouldBe(2);

        dto.InvoiceItem.Single(i => i.Id == SharedIndexItemGoldId)
            .ReverseChargeCode!.Code.ShouldBe("1");
        dto.InvoiceItem.Single(i => i.Id == SharedIndexItemConstructionId)
            .ReverseChargeCode!.Code.ShouldBe("11");
    }

    /// <summary>
    /// An invoice without a single PDP line must come back with every nested code null.
    /// Covers the short-circuit branch that skips the nested mapping altogether.
    /// </summary>
    [Fact]
    public async Task GetInvoiceByIdAsync_AllStandardItems_LeavesNestedCodeNull()
    {
        var dto = await _service.GetInvoiceByIdAsync(StandardOnlyInvoiceId);

        dto.ShouldNotBeNull();
        dto.InvoiceItem.Count.ShouldBe(2);
        dto.InvoiceItem.ShouldAllBe(i => i.ReverseChargeCode == null);
    }

    // --- Same guarantee on the list read paths ------------------------------

    /// <summary>
    /// The list endpoint runs its own query and its own MapToDto call, so it needs its own
    /// proof that the eager load is in place and the nested object survives.
    /// </summary>
    [Fact]
    public async Task GetAllInvoicesAsync_PopulatesNestedCodeOnEveryPdpLine()
    {
        var invoices = await _service.GetAllInvoicesAsync();

        var mixed = invoices.Single(i => i.DocumentNumber == "INV-MIXED-046");
        mixed.InvoiceItem.Single(i => i.Id == MixedItemConstructionId).ReverseChargeCode!.Code.ShouldBe("11");
        mixed.InvoiceItem.Single(i => i.Id == MixedItemGoldId).ReverseChargeCode!.Code.ShouldBe("1");
        mixed.InvoiceItem.Single(i => i.Id == MixedItemStandardId).ReverseChargeCode.ShouldBeNull();
    }

    /// <summary>
    /// Paged read path — same guarantee. Paging maps each page item separately, so a regression
    /// could land here without touching GetAll.
    /// </summary>
    [Fact]
    public async Task GetInvoicesPagedAsync_PopulatesNestedCodeOnEveryPdpLine()
    {
        var page = await _service.GetInvoicesPagedAsync(new InvoiceFilterDto { Page = 1, PageSize = 10 });

        var mixed = page.Items.Single(i => i.DocumentNumber == "INV-MIXED-046");
        mixed.InvoiceItem.Single(i => i.Id == MixedItemConstructionId).ReverseChargeCode!.ParagraphRef.ShouldBe("§92d");
        mixed.InvoiceItem.Single(i => i.Id == MixedItemGoldId).ReverseChargeCode!.ParagraphRef.ShouldBe("§92b");
    }

    /// <summary>
    /// Document-number lookup is a separate query used by exports and the chat tools;
    /// it must return the nested code as well.
    /// </summary>
    [Fact]
    public async Task GetInvoiceByDocumentNumberAsync_PopulatesNestedCode()
    {
        var dto = await _service.GetInvoiceByDocumentNumberAsync("INV-MIXED-046");

        dto.ShouldNotBeNull();
        dto.InvoiceItem.Single(i => i.Id == MixedItemGoldId).ReverseChargeCode!.NameCs.ShouldBe("Zlato");
    }
}
