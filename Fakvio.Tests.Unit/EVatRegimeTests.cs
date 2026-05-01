using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ZMapper;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for EVatRegime enum and InvoiceItem.VatRegime property.
///
/// Verifies that:
///   - Enum values have correct integers (never accidentally change stored DB values).
///   - Newly created InvoiceItem defaults to Standard (backward-compatible).
///   - VatRegime is persisted and retrieved correctly from InMemory DB.
///   - ZMapper maps VatRegime from InvoiceItem to InvoiceItemDto.
/// </summary>
public class EVatRegimeTests : IDisposable
{
    private readonly TenantDbContext _context;

    public EVatRegimeTests()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new TenantDbContext(options);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    // ─── Enum integer value tests ─────────────────────────────────────────────
    // These values are stored in the database as integers.
    // NEVER change them — doing so would corrupt existing data.

    [Fact]
    public void EVatRegime_Standard_HasValue0()
    {
        // Standard = 0 is the default; existing rows have this value after migration.
        ((int)EVatRegime.Standard).ShouldBe(0);
    }

    [Fact]
    public void EVatRegime_ReverseCharge_HasValue1()
    {
        // ReverseCharge = 1 — Přenesená daňová povinnost (PDP §92a–92e ZDPH)
        ((int)EVatRegime.ReverseCharge).ShouldBe(1);
    }

    [Fact]
    public void EVatRegime_Exempt_HasValue2()
    {
        // Exempt = 2 — VAT-exempt supply (e.g., financial services)
        ((int)EVatRegime.Exempt).ShouldBe(2);
    }

    [Fact]
    public void EVatRegime_OutOfScope_HasValue3()
    {
        // OutOfScope = 3 — supply entirely outside VAT scope
        ((int)EVatRegime.OutOfScope).ShouldBe(3);
    }

    // ─── Default value test ───────────────────────────────────────────────────

    [Fact]
    public void NewInvoiceItem_VatRegime_DefaultsToStandard()
    {
        // A newly constructed InvoiceItem must default to Standard without any explicit assignment.
        // This ensures backward compatibility: all existing items behave as before.
        var item = new InvoiceItem();

        item.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    // ─── Persistence tests (InMemory DB) ─────────────────────────────────────

    [Fact]
    public void InvoiceItem_VatRegime_Standard_IsPersistedAndRetrieved()
    {
        // InvoiceItem with VatRegime = Standard should survive a DB round-trip.
        var item = BuildMinimalItem(EVatRegime.Standard);
        _context.InvoiceItem.Add(item);
        _context.SaveChanges();

        var loaded = _context.InvoiceItem.AsNoTracking().First();
        loaded.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    [Fact]
    public void InvoiceItem_VatRegime_ReverseCharge_IsPersistedAndRetrieved()
    {
        // InvoiceItem with VatRegime = ReverseCharge must survive a DB round-trip.
        // This verifies the EF mapping stores and reads the correct integer (1).
        var item = BuildMinimalItem(EVatRegime.ReverseCharge);
        _context.InvoiceItem.Add(item);
        _context.SaveChanges();

        var loaded = _context.InvoiceItem.AsNoTracking().First();
        loaded.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
    }

    [Fact]
    public void InvoiceItem_VatRegime_Exempt_IsPersistedAndRetrieved()
    {
        var item = BuildMinimalItem(EVatRegime.Exempt);
        _context.InvoiceItem.Add(item);
        _context.SaveChanges();

        var loaded = _context.InvoiceItem.AsNoTracking().First();
        loaded.VatRegime.ShouldBe(EVatRegime.Exempt);
    }

    [Fact]
    public void InvoiceItem_VatRegime_OutOfScope_IsPersistedAndRetrieved()
    {
        var item = BuildMinimalItem(EVatRegime.OutOfScope);
        _context.InvoiceItem.Add(item);
        _context.SaveChanges();

        var loaded = _context.InvoiceItem.AsNoTracking().First();
        loaded.VatRegime.ShouldBe(EVatRegime.OutOfScope);
    }

    // ─── ZMapper mapping test ─────────────────────────────────────────────────

    [Fact]
    public void ZMapper_InvoiceItem_MapsVatRegimeToDto()
    {
        // ZMapper must carry VatRegime through from InvoiceItem to InvoiceItemDto.
        // If VatRegime is missing from InvoiceItemDto or the mapping, this fails.
        var item = new InvoiceItem
        {
            Description = "Test service",
            Quantity = 1,
            Unit = "pcs",
            UnitPrice = 100m,
            VatRatePercentage = 21m,
            TotalBeforeVat = 100m,
            VatAmount = 21m,
            TotalWithVat = 121m,
            VatRegime = EVatRegime.ReverseCharge
        };

        // Use ZMapper source-generated extension method (ToInvoiceItemDto is generated from InvoiceProfile)
        var dto = item.ToInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
    }

    [Fact]
    public void ZMapper_InvoiceItem_DefaultVatRegime_MapsToStandard()
    {
        // When VatRegime is not set explicitly, the mapped DTO must also have Standard.
        var item = new InvoiceItem
        {
            Description = "Default item",
            Quantity = 1,
            Unit = "pcs",
            UnitPrice = 50m,
            VatRatePercentage = 0m,
            TotalBeforeVat = 50m,
            VatAmount = 0m,
            TotalWithVat = 50m
            // VatRegime intentionally not set — should default to Standard
        };

        var dto = item.ToInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    // ─── DTO default value test ───────────────────────────────────────────────

    [Fact]
    public void CreateInvoiceItemDto_VatRegime_DefaultsToStandard()
    {
        // Client-side: CreateInvoiceItemDto must default to Standard
        // so that existing callers that don't set VatRegime get the correct value.
        var dto = new CreateInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    [Fact]
    public void InvoiceItemDto_VatRegime_DefaultsToStandard()
    {
        // Response DTO must also default to Standard (for safe deserialization of older payloads).
        var dto = new InvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    // ─── Serialization round-trip test ───────────────────────────────────────

    [Fact]
    public void EVatRegime_IntegerRoundTrip_PreservesAllValues()
    {
        // Simulate database storage: cast to int and back.
        // All four values must survive the round-trip without data loss or corruption.
        foreach (var regime in Enum.GetValues<EVatRegime>())
        {
            var stored = (int)regime;
            var loaded = (EVatRegime)stored;

            loaded.ShouldBe(regime, $"Round-trip failed for {regime} (stored as {stored})");
        }
    }

    // ─── Helper ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal valid InvoiceItem for DB persistence tests.
    /// Uses InMemoryDatabase — no FK enforcement, so InvoiceId=0 is fine.
    /// </summary>
    private static InvoiceItem BuildMinimalItem(EVatRegime vatRegime) => new()
    {
        InvoiceId = 0,          // InMemory DB does not enforce FK constraints
        Description = "Test item",
        Quantity = 1m,
        Unit = "pcs",
        UnitPrice = 100m,
        VatRatePercentage = 21m,
        TotalBeforeVat = 100m,
        VatAmount = 21m,
        TotalWithVat = 121m,
        VatRegime = vatRegime
    };
}
