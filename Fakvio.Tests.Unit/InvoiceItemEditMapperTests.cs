using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Models;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class InvoiceItemEditMapperTests
{
    [Fact]
    public void ToEditDto_PreservesReverseChargeRegimeAndCode()
    {
        var src = new InvoiceItemDto
        {
            Description = "Build", Quantity = 2, UnitPrice = 50, VatRatePercentage = 21,
            VatRegime = EVatRegime.ReverseCharge, ReverseChargeCodeId = 9, ProductCode = "P", Notes = "n"
        };

        var dto = InvoiceItemEditMapper.ToEditDto(src);

        dto.VatRegime.ShouldBe(EVatRegime.ReverseCharge);
        dto.ReverseChargeCodeId.ShouldBe(9);
        dto.Description.ShouldBe("Build");
        dto.ProductCode.ShouldBe("P");
    }
}
