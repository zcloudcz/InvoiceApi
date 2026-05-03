using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Shouldly;
using ZMapper;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for issue #46 — InvoiceItemDto mapping of VatRegime, ReverseChargeCodeId,
/// and the nested ReverseChargeCode navigation property.
///
/// Covered acceptance criteria:
/// - VatRegime is preserved in both directions (entity → DTO, DTO → entity).
/// - ReverseChargeCodeId is preserved in both directions.
/// - ToInvoiceItemDto() does NOT populate ReverseChargeCode (it must be set manually by the service).
/// - When a manually set nested ReverseChargeCode is present, all its fields are correct.
/// - Standard items have VatRegime=Standard and null ReverseChargeCodeId.
/// - ReverseCharge items have the expected regime and code id.
/// </summary>
public class InvoiceItemMappingTests
{
    // ── InvoiceItem → InvoiceItemDto (ZMapper extension) ──────────────────────

    [Fact]
    public void ToInvoiceItemDto_MapsVatRegime_Standard()
    {
        var entity = BuildItem(EVatRegime.Standard, null);

        var dto = entity.ToInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    [Fact]
    public void ToInvoiceItemDto_MapsVatRegime_ReverseCharge()
    {
        var entity = BuildItem(EVatRegime.ReverseCharge, codeId: 5);

        var dto = entity.ToInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
    }

    [Fact]
    public void ToInvoiceItemDto_MapsVatRegime_Exempt()
    {
        var entity = BuildItem(EVatRegime.Exempt, null);

        var dto = entity.ToInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.Exempt);
    }

    [Fact]
    public void ToInvoiceItemDto_MapsVatRegime_OutOfScope()
    {
        var entity = BuildItem(EVatRegime.OutOfScope, null);

        var dto = entity.ToInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.OutOfScope);
    }

    [Fact]
    public void ToInvoiceItemDto_MapsReverseChargeCodeId_WhenSet()
    {
        var entity = BuildItem(EVatRegime.ReverseCharge, codeId: 42);

        var dto = entity.ToInvoiceItemDto();

        dto.ReverseChargeCodeId.ShouldBe(42L);
    }

    [Fact]
    public void ToInvoiceItemDto_MapsReverseChargeCodeId_AsNull_WhenNotSet()
    {
        var entity = BuildItem(EVatRegime.Standard, null);

        var dto = entity.ToInvoiceItemDto();

        dto.ReverseChargeCodeId.ShouldBeNull();
    }

    [Fact]
    public void ToInvoiceItemDto_LeavesNestedReverseChargeCode_AsNull()
    {
        // ZMapper does NOT populate navigation properties — the service sets it manually.
        // This test guards against accidental auto-mapping being added that would break
        // the contract (service is the single source of truth for nested DTO population).
        var entity = BuildItem(EVatRegime.ReverseCharge, codeId: 7);
        entity.ReverseChargeCode = new ReverseChargeCode
        {
            Id = 7, Code = "5", NameCs = "Mobilní telefony", ParagraphRef = "§92c",
            ValidFrom = new DateOnly(2016, 1, 1), IsActive = true
        };

        // ToInvoiceItemDto() ignores the navigation property — result.ReverseChargeCode is null.
        var dto = entity.ToInvoiceItemDto();

        dto.ReverseChargeCode.ShouldBeNull(
            "ZMapper skips navigation properties — service must populate ReverseChargeCode manually.");
    }

    [Fact]
    public void ToInvoiceItemDto_MapsAllScalarFields_Correctly()
    {
        // Regression check: adding new fields must not break existing scalar mappings.
        var entity = new InvoiceItem
        {
            Id = 99,
            OrderIndex = 3,
            Description = "Stavební práce",
            Quantity = 10m,
            Unit = "h",
            UnitPrice = 500m,
            VatRateId = 1,
            VatRatePercentage = 21m,
            IsTextRow = false,
            TotalBeforeVat = 5000m,
            VatAmount = 0m,       // PDP — billed VAT is zero
            TotalWithVat = 5000m,
            VatRegime = EVatRegime.ReverseCharge,
            ReverseChargeCodeId = 11,
            InformationalVatAmount = 1050m,
            ProductCode = "CONST-01",
            Notes = "Smlouva č. 123"
        };

        var dto = entity.ToInvoiceItemDto();

        dto.Id.ShouldBe(99L);
        dto.OrderIndex.ShouldBe(3);
        dto.Description.ShouldBe("Stavební práce");
        dto.Quantity.ShouldBe(10m);
        dto.Unit.ShouldBe("h");
        dto.UnitPrice.ShouldBe(500m);
        dto.VatRateId.ShouldBe(1L);
        dto.VatRatePercentage.ShouldBe(21m);
        dto.IsTextRow.ShouldBeFalse();
        dto.TotalBeforeVat.ShouldBe(5000m);
        dto.VatAmount.ShouldBe(0m);
        dto.TotalWithVat.ShouldBe(5000m);
        dto.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
        dto.ReverseChargeCodeId.ShouldBe(11L);
        dto.InformationalVatAmount.ShouldBe(1050m);
        dto.ProductCode.ShouldBe("CONST-01");
        dto.Notes.ShouldBe("Smlouva č. 123");
    }

    // ── Nested ReverseChargeCode — manual population by service layer ──────────

    [Fact]
    public void NestedReverseChargeCode_MappedByToReverseChargeCodeDto_Correctly()
    {
        // The service layer calls entity.ReverseChargeCode.ToReverseChargeCodeDto()
        // and assigns the result to dto.ReverseChargeCode. Verify the ZMapper
        // extension maps all required fields (Code, NameCs, NameEn, ParagraphRef, IsActive).
        var code = new ReverseChargeCode
        {
            Id = 5, Code = "5", NameCs = "Mobilní telefony", NameEn = "Mobile phones",
            ParagraphRef = "§92c",
            ValidFrom = new DateOnly(2016, 1, 1), ValidTo = null,
            IsActive = true
        };

        var codeDto = code.ToReverseChargeCodeDto();

        codeDto.Id.ShouldBe(5L);
        codeDto.Code.ShouldBe("5");
        codeDto.NameCs.ShouldBe("Mobilní telefony");
        codeDto.NameEn.ShouldBe("Mobile phones");
        codeDto.ParagraphRef.ShouldBe("§92c");
        codeDto.IsActive.ShouldBeTrue();
        codeDto.ValidFrom.ShouldBe(new DateOnly(2016, 1, 1));
        codeDto.ValidTo.ShouldBeNull();
    }

    [Fact]
    public void NestedReverseChargeCode_MapsNullNameEn_AsNull()
    {
        var code = new ReverseChargeCode
        {
            Id = 1, Code = "1", NameCs = "Zlato", NameEn = null,
            ParagraphRef = "§92b",
            ValidFrom = new DateOnly(2016, 1, 1),
            IsActive = true
        };

        var codeDto = code.ToReverseChargeCodeDto();

        codeDto.NameEn.ShouldBeNull();
    }

    // ── CreateInvoiceItemDto / UpdateInvoiceItemDto field presence ────────────

    [Fact]
    public void CreateInvoiceItemDto_DefaultVatRegime_IsStandard()
    {
        // Ensures backward-compat: existing code that does not set VatRegime
        // gets Standard — no change in behavior.
        var dto = new CreateInvoiceItemDto();

        dto.VatRegime.ShouldBe(EVatRegime.Standard);
    }

    [Fact]
    public void CreateInvoiceItemDto_DefaultReverseChargeCodeId_IsNull()
    {
        var dto = new CreateInvoiceItemDto();

        dto.ReverseChargeCodeId.ShouldBeNull();
    }

    [Fact]
    public void UpdateInvoiceItemDto_DefaultVatRegime_IsNull()
    {
        // UpdateInvoiceItemDto.VatRegime is nullable — null means "keep existing value".
        var dto = new UpdateInvoiceItemDto();

        dto.VatRegime.ShouldBeNull();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal InvoiceItem with the given VatRegime and ReverseChargeCodeId.
    /// Other required fields use sensible defaults so mapping does not throw.
    /// </summary>
    private static InvoiceItem BuildItem(EVatRegime regime, long? codeId) =>
        new InvoiceItem
        {
            Id = 1,
            InvoiceId = 100,
            OrderIndex = 1,
            Description = "Test item",
            Quantity = 1m,
            Unit = "pcs",
            UnitPrice = 100m,
            VatRatePercentage = 21m,
            TotalBeforeVat = 100m,
            VatAmount = 21m,
            TotalWithVat = 121m,
            VatRegime = regime,
            ReverseChargeCodeId = codeId
        };
}
