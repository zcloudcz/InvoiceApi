using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fakvio.Contracts.Common;

namespace Fakvio.Application.QrPayment;

/// <summary>
/// Builds a SEPA Credit Transfer QR code payload per the EPC069-12 (v2/v3) "EPC QR Code" standard.
///
/// Used for EUR invoices with an IBAN — the pan-European equivalent of the Czech QR Platba (SPD).
/// Any SEPA banking app (not just Czech ones) can scan it and prefill a credit transfer.
///
/// Format: 11 fixed lines joined by LF (the 12th, optional "beneficiary to originator
/// information" line, is omitted since it is unused here):
///   BCD / 002 / 1 (UTF-8) / SCT / BIC / Beneficiary name / IBAN / Amount /
///   Purpose (empty) / Structured reference (empty) / Unstructured remittance
///
/// Reference: EPC069-12 "Quick Response Code Guidelines to Enable Data Capture for the
/// Initiation of a SEPA Credit Transfer" — European Payments Council.
/// </summary>
public static class EpcQrBuilder
{
    private const int MaxNameLength = 70;
    private const int MaxRemittanceLength = 140;
    private const int MaxTotalBytes = 331;
    private const decimal MinAmount = EpcQrPolicy.MinAmount;
    private const decimal MaxAmount = EpcQrPolicy.MaxAmount;
    private static readonly Regex BicPattern = new("^[A-Z0-9]{8}([A-Z0-9]{3})?$", RegexOptions.Compiled);

    /// <summary>True when the amount can be expressed in an EPC QR (0.01 - 999,999,999.99).</summary>
    public static bool IsAmountSupported(decimal amount) => EpcQrPolicy.IsAmountSupported(amount);

    /// <summary>
    /// Builds the EPC QR payload string, ready to be encoded into a QR code (ECC level M).
    /// </summary>
    /// <param name="beneficiaryName">Payee (issuer) name — truncated to 70 chars per spec.</param>
    /// <param name="iban">Payee IBAN. Spaces and dashes are stripped automatically.</param>
    /// <param name="bic">Optional BIC/SWIFT — allowed empty since version 002 made it optional.</param>
    /// <param name="amount">Payment amount in EUR. Must be between 0.01 and 999,999,999.99.</param>
    /// <param name="documentNumber">Invoice number, used to build the remittance text.</param>
    /// <param name="variableSymbol">Variable symbol, used to build the remittance text.</param>
    /// <returns>The LF-joined EPC QR payload.</returns>
    public static string Build(string beneficiaryName, string iban, string? bic, decimal amount,
        string? documentNumber, string? variableSymbol)
    {
        if (string.IsNullOrWhiteSpace(iban))
        {
            throw new ArgumentException("IBAN is required for an EPC QR code.", nameof(iban));
        }

        if (amount < MinAmount || amount > MaxAmount)
        {
            throw new ArgumentOutOfRangeException(nameof(amount),
                $"EPC QR amount must be between {MinAmount:F2} and {MaxAmount:F2} EUR.");
        }

        // Spaces/dashes stripped — same cleanup rule as the Czech SPD builder (users paste
        // IBANs copied from bank statements with separators).
        var cleanIban = iban.Replace(" ", "").Replace("-", "");
        var name = Truncate(beneficiaryName ?? string.Empty, MaxNameLength);
        var cleanBic = NormalizeBic(bic);
        var amountText = "EUR" + amount.ToString("F2", CultureInfo.InvariantCulture);
        var remittance = Truncate(BuildRemittanceText(documentNumber, variableSymbol), MaxRemittanceLength);

        // Trim the remittance to whatever UTF-8 byte budget is left (non-ASCII names/text take
        // 2-4 bytes per char) so the payload never exceeds the 331-byte spec limit.
        var fixedBytes = Encoding.UTF8.GetByteCount(
            string.Join("\n", "BCD", "002", "1", "SCT", cleanBic, name, cleanIban, amountText, "", "", ""));
        remittance = TruncateToBytes(remittance, MaxTotalBytes - fixedBytes);

        var lines = new[]
        {
            "BCD",                                              // Service tag
            "002",                                              // Version
            "1",                                                // Character set: 1 = UTF-8
            "SCT",                                               // Identification: SEPA Credit Transfer
            cleanBic,                                            // BIC — optional in v002
            name,                                                // Beneficiary name
            cleanIban,                                           // Beneficiary IBAN
            "EUR" + amount.ToString("F2", CultureInfo.InvariantCulture), // Amount: "EUR" + up to 2 decimals
            string.Empty,                                        // Purpose — not used
            string.Empty,                                        // Structured creditor reference — not used
            remittance                                           // Unstructured remittance information
        };

        var payload = string.Join("\n", lines);

        var byteLength = Encoding.UTF8.GetByteCount(payload);
        if (byteLength > MaxTotalBytes)
        {
            // Should not happen given the per-field limits above, but the spec hard-caps the
            // total payload — fail loudly instead of generating an unscannable QR code.
            throw new InvalidOperationException(
                $"EPC QR payload exceeds the {MaxTotalBytes}-byte limit ({byteLength} bytes).");
        }

        return payload;
    }

    /// <summary>
    /// Builds the human-readable remittance text, e.g. "Faktura INV2026001 VS 2026001".
    /// </summary>
    private static string BuildRemittanceText(string? documentNumber, string? variableSymbol)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(documentNumber))
        {
            parts.Add($"Faktura {documentNumber.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(variableSymbol))
        {
            parts.Add($"VS {variableSymbol.Trim()}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Strips whitespace, upper-cases, and drops anything that is not a valid 8/11-char BIC.</summary>
    private static string NormalizeBic(string? bic)
    {
        var cleaned = new string((bic ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return BicPattern.IsMatch(cleaned) ? cleaned : string.Empty;
    }

    /// <summary>Truncates to at most maxLength UTF-16 chars without splitting a surrogate pair.</summary>
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        if (char.IsHighSurrogate(value[maxLength - 1])) maxLength--;
        return value[..maxLength];
    }

    /// <summary>Truncates to at most maxBytes UTF-8 bytes, never cutting through a character.</summary>
    private static string TruncateToBytes(string value, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var b = rune.Utf8SequenceLength;
            if (bytes + b > maxBytes) break;
            sb.Append(rune.ToString());
            bytes += b;
        }
        return sb.ToString();
    }
}
