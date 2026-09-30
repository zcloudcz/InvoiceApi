using System.Text.RegularExpressions;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;

namespace Fakvio.Infrastructure.Service.Ubl;

/// <summary>
/// Pure code/lookup helpers for the UBL 2.1 / Peppol BIS Billing 3.0 export (ADR 0002, F1.1).
/// No database access, no I/O — every method is a deterministic function of its input, so
/// <see cref="UblMapper"/> (F1.3+) can stay a pure mapper too.
///
/// Junior note: every table here mirrors a row in the ADR (§4.1.2) — if a rule changes,
/// change it here and the theory tests in <c>UblCodesTests</c> will tell you what broke.
/// </summary>
internal static class UblCodes
{
    // --------------------------------------------------------------------------
    // Country (BT-40/BT-55 — ISO 3166-1 alpha-2)
    // --------------------------------------------------------------------------

    /// <summary>
    /// Known free-text country names (CZ/SK/EN spellings) that Fakvio's address fields
    /// currently store instead of an ISO code. Keyed case-insensitively.
    /// </summary>
    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Česká republika"] = "CZ",
        ["Česko"] = "CZ",
        ["Czech Republic"] = "CZ",
        ["Slovensko"] = "SK",
        ["Slovenská republika"] = "SK",
        ["Slovakia"] = "SK",
        ["Německo"] = "DE",
        ["Deutschland"] = "DE",
        ["Rakousko"] = "AT",
        ["Österreich"] = "AT",
        ["Polsko"] = "PL",
        ["Polska"] = "PL"
    };

    /// <summary>
    /// Normalizes a free-text or ISO country value to an ISO 3166-1 alpha-2 code.
    /// Empty input defaults to "CZ" (Fakvio's primary audience, same default the address
    /// forms already use). A value that is neither empty, a 2-letter code, nor a known name
    /// returns null — the caller (pre-flight, F1.5) turns that into a blocking issue instead
    /// of guessing.
    /// </summary>
    internal static string? CountryToIso2(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return "CZ";

        var trimmed = country.Trim();
        if (trimmed.Length == 2)
            return trimmed.ToUpperInvariant();

        return CountryNames.TryGetValue(trimmed, out var code) ? code : null;
    }

    // --------------------------------------------------------------------------
    // Unit of measure (BT-130 — UN/ECE Recommendation 20)
    // --------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ks"] = "H87", ["kus"] = "H87", ["pcs"] = "H87", ["pc"] = "H87",
        ["hod"] = "HUR", ["h"] = "HUR", ["hr"] = "HUR",
        ["min"] = "MIN",
        ["den"] = "DAY", ["d"] = "DAY", ["day"] = "DAY",
        ["měs"] = "MON", ["mes"] = "MON", ["month"] = "MON",
        ["rok"] = "ANN", ["year"] = "ANN",
        ["km"] = "KMT",
        ["m"] = "MTR",
        ["m2"] = "MTK", ["m²"] = "MTK",
        ["m3"] = "MTQ", ["m³"] = "MTQ",
        ["kg"] = "KGM",
        ["g"] = "GRM",
        ["t"] = "TNE",
        ["l"] = "LTR",
        ["bal"] = "XPK",
        ["kpl"] = "SET", ["sada"] = "SET", ["set"] = "SET"
    };

    /// <summary>
    /// Maps Fakvio's free-text <c>InvoiceItem.Unit</c> to a UN/ECE Rec 20 unit code.
    /// Case-insensitive, trailing dot ignored ("ks." same as "ks"). A value that already
    /// looks like a valid 3-letter uppercase code is passed through unchanged (covers units
    /// a user already entered correctly). Anything unrecognized falls back to "C62" (piece —
    /// the UN/CEFACT catch-all "one"), never blocks the export.
    /// </summary>
    internal static string UnitToRec20(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return "C62";

        var trimmed = unit.Trim().TrimEnd('.');
        if (Units.TryGetValue(trimmed, out var code))
            return code;

        if (trimmed.Length == 3 && trimmed.All(c => c is >= 'A' and <= 'Z'))
            return trimmed;

        return "C62";
    }

    // --------------------------------------------------------------------------
    // VAT category (BT-151 / BT-118) + exemption reason (BT-120/BT-121)
    // --------------------------------------------------------------------------

    /// <summary>Machine-readable keys for <see cref="ExemptionText"/> — kept separate from the
    /// display text so <see cref="VatCategory"/> stays language-agnostic and testable.</summary>
    internal static class ExemptionTextKeys
    {
        internal const string NotVatPayer = "NotVatPayer";
        internal const string Exempt = "Exempt";
        internal const string ReverseCharge = "ReverseCharge";
    }

    /// <summary>Result of <see cref="VatCategory"/> — the four BT fields it decides together.</summary>
    internal readonly record struct VatCategoryResult(
        string Code, decimal? Percent, string? ExemptionCode, string? ExemptionTextKey);

    /// <summary>
    /// Determines the Peppol BIS VAT category (BT-151/BT-118) for one invoice line, following
    /// the rules table in ADR 0002 §4.1.2.
    ///
    /// The issuer's VAT-payer flag always wins: a non-payer never charges VAT, so every line
    /// on their invoices is category "O" regardless of the line's own <see cref="EVatRegime"/>
    /// (this mirrors <c>NonVatPayerItems.StripVat</c>, which already guarantees a non-payer's
    /// items carry no VAT rate/reverse-charge data — this method just derives the UBL category
    /// from that same fact instead of duplicating the stripping logic).
    /// </summary>
    internal static VatCategoryResult VatCategory(EVatRegime regime, decimal rate, bool issuerIsVatPayer)
    {
        if (!issuerIsVatPayer)
            return new VatCategoryResult("O", null, "VATEX-EU-O", ExemptionTextKeys.NotVatPayer);

        return regime switch
        {
            EVatRegime.ReverseCharge =>
                new VatCategoryResult("AE", 0m, "VATEX-EU-AE", ExemptionTextKeys.ReverseCharge),
            EVatRegime.Exempt =>
                new VatCategoryResult("E", 0m, null, ExemptionTextKeys.Exempt),
            EVatRegime.OutOfScope =>
                new VatCategoryResult("O", null, "VATEX-EU-O", ExemptionTextKeys.NotVatPayer),
            _ => rate == 0m
                ? new VatCategoryResult("E", 0m, null, ExemptionTextKeys.Exempt)
                : new VatCategoryResult("S", rate, null, null)
        };
    }

    /// <summary>
    /// Localizes an <see cref="ExemptionTextKeys"/> value for BT-120 (VAT exemption reason
    /// text). Only "cs" and "sk" get Czech/Slovak wording (they read the same to a Czech or
    /// Slovak buyer) — every other <c>Client.Language</c> value falls back to English, matching
    /// how the rest of the document generation (PDF templates) treats unknown languages.
    /// Returns null when <paramref name="textKey"/> is null (categories S/AE-without-text/E
    /// pass no key).
    /// </summary>
    internal static string? ExemptionText(string? textKey, string language)
    {
        if (textKey is null)
            return null;

        var czechFamily = language is "cs" or "sk";
        return textKey switch
        {
            ExemptionTextKeys.NotVatPayer => czechFamily ? "Nepodléhá DPH" : "Not subject to VAT",
            ExemptionTextKeys.Exempt => czechFamily ? "Osvobozeno od DPH" : "Exempt from VAT",
            ExemptionTextKeys.ReverseCharge => czechFamily ? "Přenesení daňové povinnosti" : "Reverse charge",
            _ => null
        };
    }

    // --------------------------------------------------------------------------
    // Payment means (BT-81 — UNCL4461)
    // --------------------------------------------------------------------------

    /// <summary>
    /// Maps Fakvio's <see cref="EPaymentMethod"/> to the UNCL4461 code Peppol BIS expects.
    /// Bank transfer is "58" (SEPA credit transfer) only when the payment carries an IBAN
    /// *and* the invoice currency is EUR — otherwise "30" (generic credit transfer), matching
    /// BR-61 (SEPA requires IBAN) without asserting SEPA for a non-EUR bank transfer.
    /// </summary>
    internal static string PaymentMeansCode(EPaymentMethod? method, bool hasIban, bool currencyIsEur) =>
        method switch
        {
            EPaymentMethod.BankTransfer => hasIban && currencyIsEur ? "58" : "30",
            EPaymentMethod.Cash => "10",
            EPaymentMethod.CreditCard => "48",
            _ => "ZZZ" // PayPal, Other, or not set — Peppol's "mutually defined" fallback.
        };

    // --------------------------------------------------------------------------
    // Peppol participant ID / EAS endpoint (BT-34 / BT-49)
    // --------------------------------------------------------------------------

    /// <summary>Scheme + value pair identifying a Peppol network participant.</summary>
    internal readonly record struct EndpointIdResult(string SchemeId, string Value);

    // SK DIČ: 10 digits, optionally prefixed with "SK" (both forms appear on Fakvio clients —
    // a plain 10-digit RegistrationNumber-like value for a non-payer, or the full "SKxxxxxxxxxx").
    private static readonly Regex SkTaxNumber = new(@"^(?:SK)?(\d{10})$", RegexOptions.Compiled);

    // CZ DIČ: "CZ" + 8-10 digits (the extra digits cover VAT groups / natural persons' RČ-based DIČ).
    private static readonly Regex CzTaxNumber = new(@"^CZ\d{8,10}$", RegexOptions.Compiled);

    /// <summary>
    /// Derives the Peppol endpoint ID (scheme + value, e.g. "0245:2020123456") for a client.
    ///
    /// Precedence, per ADR 0002 §4.1.2: (1) <see cref="Client.PeppolId"/> — a manual override
    /// (F1.8) for cases the automatic derivation gets wrong or cannot reach at all (a VAT
    /// group, a foreign client outside CZ/SK, …); (2)/(3) derived from the country and tax
    /// number below.
    ///
    /// Returns null when neither the override nor the country/tax-number combination gives a
    /// reliable scheme — the caller (pre-flight, F1.5) turns that into a blocking issue rather
    /// than guessing "9950".
    /// </summary>
    internal static EndpointIdResult? EndpointId(Client? client)
    {
        if (client is null)
            return null;

        if (!string.IsNullOrWhiteSpace(client.PeppolId))
        {
            var separatorIndex = client.PeppolId.IndexOf(':');
            // The DTO-level [RegularExpression] already rejects a PeppolId without a colon, but
            // this method has no I/O and must not throw on a value that slipped through some
            // other path (a direct DB edit, an older row) — fall through to derivation instead.
            if (separatorIndex > 0)
                return new EndpointIdResult(client.PeppolId[..separatorIndex], client.PeppolId[(separatorIndex + 1)..]);
        }

        var address = client.Address?.FirstOrDefault(a => a.IsPrimary) ?? client.Address?.FirstOrDefault();
        var country = CountryToIso2(address?.Country);
        var taxNumber = client.TaxNumber?.Trim();

        if (string.IsNullOrWhiteSpace(taxNumber))
            return null;

        if (country == "SK")
        {
            var match = SkTaxNumber.Match(taxNumber);
            if (match.Success)
                return new EndpointIdResult("0245", match.Groups[1].Value);
        }

        if (country == "CZ" && CzTaxNumber.IsMatch(taxNumber))
            return new EndpointIdResult("9929", taxNumber);

        return null;
    }
}
