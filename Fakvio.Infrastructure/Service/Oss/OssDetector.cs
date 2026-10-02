using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.Oss;

/// <summary>
/// Pure function that decides whether an invoice falls under the EU OSS (One-Stop-Shop)
/// special scheme — no database access, no I/O, so it is trivially unit-testable (mirrors
/// the UblCodes "pure lookup helper" pattern, ADR 0002 §4.1.2).
///
/// OSS applies when ALL of the following hold (DEVGUIDE §4.15):
/// 1. The issuer is registered for OSS (<c>CompanySystemSettings.OssRegistered</c>).
/// 2. The issuer is a CZ VAT payer (OSS only makes sense for a VAT payer).
/// 3. The document is an actual tax document — Invoice or TaxReceiptForAdvance.
///    (Proforma creates no VAT obligation yet — see EDocumentType.Proforma — so it is
///    never itself an OSS document; CreditNote inherits its parent's OssCountryCode
///    instead of being re-detected — see InvoiceService.)
/// 4. The client is a consumer (B2C): no VAT/tax number and not itself flagged as a VAT payer.
///    A business client (B2B) uses ordinary intra-community reverse-charge rules (#43/#45),
///    never OSS.
/// 5. The client's country resolves to an EU member state OTHER than Czechia. Sales to CZ
///    consumers are ordinary domestic DPH; sales to non-EU consumers are exports, outside
///    OSS entirely.
///
/// Out of scope (documented limitation, not enforced here): the EUR 10 000/year
/// cross-border threshold below which a micro-business may still charge its home country's
/// VAT instead of using OSS. See DEVGUIDE §4.15 "Known limitations".
/// </summary>
public static class OssDetector
{
    /// <summary>
    /// Returns the ISO 3166-1 alpha-2 destination country code if this is an OSS case,
    /// otherwise null.
    /// </summary>
    public static string? DetermineCountryCode(
        bool issuerOssRegistered,
        bool issuerIsVatPayer,
        Client? client,
        EDocumentType documentType)
    {
        if (documentType != EDocumentType.Invoice && documentType != EDocumentType.TaxReceiptForAdvance)
            return null;

        if (!issuerOssRegistered || !issuerIsVatPayer)
            return null;

        if (client == null)
            return null;

        // B2C only — a client with a tax number, or flagged as a VAT payer, is a business.
        if (!string.IsNullOrWhiteSpace(client.TaxNumber) || client.IsVatPayer)
            return null;

        var address = client.Address?.FirstOrDefault(a => a.IsPrimary) ?? client.Address?.FirstOrDefault();
        var countryCode = EuCountries.ToIso2(address?.Country);

        return EuCountries.IsOtherMemberState(countryCode) ? countryCode!.ToUpperInvariant() : null;
    }
}
