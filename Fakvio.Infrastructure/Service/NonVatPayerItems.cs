using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// A company that is NOT registered for VAT (neplátce DPH) must never charge VAT.
///
/// Callers do not reliably know that: the invoice form pre-selects the default 21 % rate on
/// every new row, templates and MCP clients copy whatever rate they have. So instead of
/// trusting every caller, the services that save invoice and template items call
/// <see cref="StripVat"/> for a non-VAT-payer issuer — one rule, applied where every
/// caller routes through (InvoiceService create/update, InvoiceTemplateService create/update).
/// </summary>
internal static class NonVatPayerItems
{
    /// <summary>
    /// Clears every VAT-related field on the items, so the totals come out as
    /// "price without VAT = price with VAT" and no VAT rate is stored.
    /// </summary>
    public static void StripVat(IEnumerable<CreateInvoiceItemDto> items)
    {
        foreach (var item in items)
        {
            item.VatRateId = null;
            item.VatRatePercentage = 0;
            // Reverse charge is a VAT-payer regime (it also computes an informational VAT
            // amount), so it falls back to Standard. Exempt / OutOfScope carry no VAT and stay.
            if (item.VatRegime == EVatRegime.ReverseCharge)
                item.VatRegime = EVatRegime.Standard;
            item.ReverseChargeCodeId = null;
        }
    }
}
