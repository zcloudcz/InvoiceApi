// ============================================================================
// InvoiceServiceNullReverseChargeTests — coverage for issue #99 AC #5.
//
// Verifies that invoices with null ReverseChargeCode (Standard/Exempt/OutOfScope
// items) are returned normally by GetInvoicesPagedAsync and GetAllInvoicesAsync
// without throwing — even when InvoiceItem.ReverseChargeCode navigation is null.
//
// Before the fix: if migration Add_ReverseChargeFk_v45 failed silently, EF Core
// would crash on the missing column. After the migration is reliably applied,
// Standard items have ReverseChargeCodeId = null and ReverseChargeCode = null;
// this is valid and must NOT cause a NullReferenceException in MapToDto or the
// ZMapper-generated ToInvoiceDto().
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
/// Tests that InvoiceService read methods handle invoices with null ReverseChargeCode
/// gracefully — returning the invoice data normally without crashing.
///
/// Why this matters (AC #5 from issue #99):
/// The ReverseChargeCode navigation property on InvoiceItem is intentionally nullable —
/// only VatRegime == ReverseCharge items have a code, all others have null. After the
/// Add_ReverseChargeFk_v45 migration is applied, the DB stores null for Standard items.
/// The service and mapping layer must tolerate this without throwing.
/// </summary>
public class InvoiceServiceNullReverseChargeTests : IDisposable
{
    private readonly TenantDbContext _context;
    private readonly InvoiceService _service;

    // IDs for seed data
    private const long ClientId = 1;
    private const long IssuerId = 2;
    private const long CurrencyId = 1;
    private const long VatRateId = 1;
    private const long ReverseChargeCodeId = 10;

    public InvoiceServiceNullReverseChargeTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);

        _service = new InvoiceService(
            _context,
            Substitute.For<INumberSequenceService>(),
            Substitute.For<ITenantReadinessService>(),
            Substitute.For<ILogger<InvoiceService>>());

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
        // Clients — must be saved before Invoice rows due to InMemoryDb FK resolution
        _context.Client.Add(new Client
        {
            Id = ClientId, CompanyName = "Odběratel s.r.o.", RegistrationNumber = "CZ11111111",
            IsIssuer = false, IsActive = true
        });
        _context.Client.Add(new Client
        {
            Id = IssuerId, CompanyName = "Dodavatel a.s.", RegistrationNumber = "CZ22222222",
            IsIssuer = true, IsActive = true, IsVatPayer = true
        });
        _context.SaveChanges();

        _context.Currency.Add(new Currency
        {
            Id = CurrencyId, Code = "CZK", Name = "Czech Koruna", Symbol = "Kč",
            DecimalPlaces = 2, SortOrder = 1, IsActive = true
        });
        _context.VatRate.Add(new VatRate
        {
            Id = VatRateId, Name = "Základní sazba 21%", Rate = 21,
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

        // Invoice with ONLY Standard items (no ReverseChargeCode navigation)
        _context.Invoice.Add(new Invoice
        {
            Id = 1,
            DocumentNumber = "INV-STANDARD-001",
            ClientId = ClientId, IssuerId = IssuerId, CurrencyId = CurrencyId,
            Status = EInvoiceStatus.Draft,
            DocumentType = EDocumentType.Invoice,
            IssueDate = DateTime.UtcNow,
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    // Standard item — ReverseChargeCodeId = null, ReverseChargeCode = null
                    OrderIndex = 1,
                    Description = "Konzultační služby",
                    Quantity = 10, Unit = "hod", UnitPrice = 100,
                    VatRateId = VatRateId, VatRatePercentage = 21,
                    TotalBeforeVat = 1000, VatAmount = 210, TotalWithVat = 1210,
                    VatRegime = EVatRegime.Standard,
                    ReverseChargeCodeId = null,   // explicitly null — Standard regime
                    InformationalVatAmount = 0
                }
            }
        });
        _context.SaveChanges();

        // Invoice with MIXED Standard + ReverseCharge items
        _context.Invoice.Add(new Invoice
        {
            Id = 2,
            DocumentNumber = "INV-MIXED-001",
            ClientId = ClientId, IssuerId = IssuerId, CurrencyId = CurrencyId,
            Status = EInvoiceStatus.Draft,
            DocumentType = EDocumentType.Invoice,
            IssueDate = DateTime.UtcNow,
            InvoiceItem = new List<InvoiceItem>
            {
                new()
                {
                    OrderIndex = 1,
                    Description = "Konzultační služby (Standard)",
                    Quantity = 5, Unit = "hod", UnitPrice = 200,
                    VatRateId = VatRateId, VatRatePercentage = 21,
                    TotalBeforeVat = 1000, VatAmount = 210, TotalWithVat = 1210,
                    VatRegime = EVatRegime.Standard,
                    ReverseChargeCodeId = null,
                    InformationalVatAmount = 0
                },
                new()
                {
                    OrderIndex = 2,
                    Description = "Stavební práce §92d (ReverseCharge)",
                    Quantity = 5, Unit = "hod", UnitPrice = 200,
                    VatRateId = VatRateId, VatRatePercentage = 21,
                    TotalBeforeVat = 1000, VatAmount = 0, TotalWithVat = 1000,
                    VatRegime = EVatRegime.ReverseCharge,
                    ReverseChargeCodeId = ReverseChargeCodeId,  // FK set for PDP item
                    InformationalVatAmount = 210                 // buyer self-assesses this
                }
            }
        });
        _context.SaveChanges();
    }

    // ─── GetInvoicesPagedAsync ────────────────────────────────────────────────

    /// <summary>
    /// GetInvoicesPagedAsync must return invoices with Standard items (null ReverseChargeCode)
    /// without throwing — AC #5 from issue #99.
    ///
    /// Before the fix: if ThenInclude for ReverseChargeCode was missing, EF Core could
    /// generate additional N+1 queries or throw on lazy-loading attempts. With the fix
    /// (explicit ThenInclude + nullable navigation), the result is returned cleanly.
    /// </summary>
    [Fact]
    public async Task GetInvoicesPagedAsync_InvoiceWithNullReverseCharge_ReturnsNormally()
    {
        // Arrange — filter by the standard-only invoice
        var filter = new InvoiceFilterDto { Page = 1, PageSize = 10 };

        // Act — must NOT throw
        var result = await _service.GetInvoicesPagedAsync(filter);

        // Assert — both invoices returned; Standard items don't crash the mapping
        result.Items.Count.ShouldBe(2);
        result.Items.ShouldContain(i => i.DocumentNumber == "INV-STANDARD-001");
        result.Items.ShouldContain(i => i.DocumentNumber == "INV-MIXED-001");
    }

    /// <summary>
    /// Standard invoice items must have ReverseChargeCodeId = null in the DTO.
    /// Mixed-regime invoice must have both items returned correctly.
    /// </summary>
    [Fact]
    public async Task GetInvoicesPagedAsync_StandardItem_HasNullReverseChargeCodeId()
    {
        // Arrange
        var filter = new InvoiceFilterDto { Page = 1, PageSize = 10 };

        // Act
        var result = await _service.GetInvoicesPagedAsync(filter);

        // Assert — standard-only invoice items have no reverse charge code
        var standardInvoice = result.Items.Single(i => i.DocumentNumber == "INV-STANDARD-001");
        standardInvoice.InvoiceItem.ShouldHaveSingleItem();
        standardInvoice.InvoiceItem[0].ReverseChargeCodeId.ShouldBeNull();
        standardInvoice.InvoiceItem[0].VatRegime.ShouldBe(EVatRegime.Standard);
        standardInvoice.InvoiceItem[0].InformationalVatAmount.ShouldBe(0m);
    }

    /// <summary>
    /// Mixed invoice must have the Standard item with null code and the PDP item with the code set.
    /// Both items coexist on the same invoice without crashing.
    /// </summary>
    [Fact]
    public async Task GetInvoicesPagedAsync_MixedInvoice_BothRegimesReturnedCorrectly()
    {
        // Arrange
        var filter = new InvoiceFilterDto { Page = 1, PageSize = 10 };

        // Act
        var result = await _service.GetInvoicesPagedAsync(filter);

        // Assert — mixed invoice has 2 items with different regimes
        var mixedInvoice = result.Items.Single(i => i.DocumentNumber == "INV-MIXED-001");
        mixedInvoice.InvoiceItem.Count.ShouldBe(2);

        var standardItem = mixedInvoice.InvoiceItem.Single(item => item.VatRegime == EVatRegime.Standard);
        standardItem.ReverseChargeCodeId.ShouldBeNull();
        standardItem.InformationalVatAmount.ShouldBe(0m);

        var reverseChargeItem = mixedInvoice.InvoiceItem.Single(item => item.VatRegime == EVatRegime.ReverseCharge);
        reverseChargeItem.ReverseChargeCodeId.ShouldBe(ReverseChargeCodeId);
        reverseChargeItem.InformationalVatAmount.ShouldBe(210m);
    }

    // ─── GetAllInvoicesAsync ──────────────────────────────────────────────────

    /// <summary>
    /// GetAllInvoicesAsync must also handle null ReverseChargeCode without crashing.
    /// </summary>
    [Fact]
    public async Task GetAllInvoicesAsync_InvoiceWithNullReverseCharge_ReturnsNormally()
    {
        // Act — must NOT throw
        var result = await _service.GetAllInvoicesAsync();

        // Assert
        result.Count.ShouldBe(2);
        result.ShouldContain(i => i.DocumentNumber == "INV-STANDARD-001");
        result.ShouldContain(i => i.DocumentNumber == "INV-MIXED-001");
    }
}
