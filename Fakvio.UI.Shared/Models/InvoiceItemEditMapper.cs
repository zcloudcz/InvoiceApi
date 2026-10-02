using Fakvio.Contracts.Dto.Invoice;
namespace Fakvio.UI.Shared.Models;

/// <summary>
/// Projects a stored invoice/template item into the editable DTO sent back on save.
/// VatRegime and ReverseChargeCodeId MUST be copied — otherwise saving an edited reverse charge
/// invoice silently falls back to Standard VAT (21 %) and loses the PDP note.
/// </summary>
public static class InvoiceItemEditMapper
{
    public static CreateInvoiceItemDto ToEditDto(InvoiceItemDto i) => new()
    {
        OrderIndex = i.OrderIndex,
        IsTextRow = i.IsTextRow,
        Description = i.Description,
        Quantity = i.Quantity,
        Unit = i.Unit,
        UnitPrice = i.UnitPrice,
        VatRateId = i.VatRateId,
        VatRatePercentage = i.VatRatePercentage,
        VatRegime = i.VatRegime,
        ReverseChargeCodeId = i.ReverseChargeCodeId,
        ProductCode = i.ProductCode,
        Notes = i.Notes
    };
}
