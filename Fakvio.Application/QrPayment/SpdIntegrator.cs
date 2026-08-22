using System.Text;
using Fakvio.Domain.Validation;

namespace Fakvio.Application.QrPayment;

/// <summary>
/// Integrates QR Faktura (SIND) data into a QR Platba (SPD) payment string.
///
/// The integration follows the official specification:
/// 1. Shared keys (ACC, AM, CC, DT) are extracted from SIND and placed in SPD
/// 2. VS from SIND becomes X-VS in SPD
/// 3. Remaining SIND attributes are URL-encoded and placed as X-INV in SPD
/// 4. MSG can exist independently in both
///
/// The result is a single SPD string that contains both payment and invoice data,
/// generating ONE QR code instead of two.
///
/// Reference: https://www.kdpcr.cz/informace/qr-faktura/integrace-s-qr-platbou
/// </summary>
public static class SpdIntegrator
{
    /// <summary>
    /// Keys that are shared between SIND and SPD formats.
    /// These are extracted from SIND and placed directly into the SPD string.
    /// </summary>
    private static readonly HashSet<string> SharedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACC", "AM", "CC", "DT"
    };

    /// <summary>
    /// Builds a simple SPD (QR Platba) string with payment data only — no X-INV invoice data.
    ///
    /// This generates a pure, banking-app-compatible SPD string that most Czech banking apps
    /// can reliably scan. Unlike BuildSpdWithInvoice (which embeds SIND as X-INV), this method
    /// produces a short, simple string containing only payment-relevant attributes.
    ///
    /// Output example: SPD*1.0*ACC:CZ5855000000001265098001+RZBCCZPP*AM:5850.00*CC:CZK*DT:20260225*MSG:INV2026001*X-VS:2026001
    /// </summary>
    /// <param name="iban">IBAN of the payee (required). Spaces and dashes are stripped automatically.</param>
    /// <param name="swift">Optional BIC/SWIFT code — appended as +SWIFT after IBAN.</param>
    /// <param name="amount">Payment amount (formatted to 2 decimal places with dot separator).</param>
    /// <param name="currencyCode">Optional ISO 4217 currency code (e.g., "CZK").</param>
    /// <param name="dueDate">Optional payment due date (formatted as YYYYMMDD).</param>
    /// <param name="variableSymbol">Optional variable symbol — becomes X-VS in SPD.</param>
    /// <param name="message">Optional message/document number (max 60 chars per SPD spec).</param>
    /// <returns>Simple SPD string ready for QR code encoding.</returns>
    /// <exception cref="ArgumentException">
    /// The IBAN is missing or invalid. Issue #154: this method used to strip the separators
    /// and copy whatever was left into ACC, so a typo silently produced a QR code that no
    /// banking app could pay. The payment string is a system boundary — it fails fast here
    /// rather than in the recipient's bank app.
    /// </exception>
    public static string BuildSimpleSpdString(
        string iban, string? swift, decimal amount, string? currencyCode,
        DateTime? dueDate, string? variableSymbol, string? message)
    {
        if (!BankAccountValidator.TryValidateIban(iban, out var ibanError))
        {
            throw new ArgumentException(
                $"Cannot build an SPD payment string: {ibanError}", nameof(iban));
        }

        var attributes = new Dictionary<string, string>();

        // ACC is required — the normalized form drops the separators users paste along
        var cleanIban = BankAccountValidator.NormalizeIban(iban);
        var accValue = !string.IsNullOrWhiteSpace(swift)
            ? $"{cleanIban}+{swift.Trim()}"
            : cleanIban;
        attributes["ACC"] = accValue;

        // AM — amount with 2 decimal places, invariant dot separator
        attributes["AM"] = amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // CC — optional ISO 4217 currency code (uppercase)
        if (!string.IsNullOrWhiteSpace(currencyCode))
        {
            attributes["CC"] = currencyCode.Trim().ToUpperInvariant();
        }

        // DT — optional due date as YYYYMMDD
        if (dueDate.HasValue)
        {
            attributes["DT"] = dueDate.Value.ToString("yyyyMMdd");
        }

        // MSG — optional message / document number (max 60 chars per SPD spec)
        if (!string.IsNullOrWhiteSpace(message))
        {
            attributes["MSG"] = message.Length > 60 ? message[..60] : message;
        }

        // X-VS — optional variable symbol (Czech payment identifier)
        if (!string.IsNullOrWhiteSpace(variableSymbol))
        {
            attributes["X-VS"] = variableSymbol.Trim();
        }

        return BuildSpdString(attributes);
    }

    /// <summary>
    /// Builds a combined SPD string with integrated SIND invoice data.
    ///
    /// NOTE: This method is kept for potential future use but is no longer called by QrPaymentService.
    /// Most Czech banking apps reject the long X-INV-encoded strings.
    /// Use BuildSimpleSpdString for standard QR Platba generation.
    ///
    /// Process:
    /// 1. Extract shared keys (ACC, AM, CC, DT) from SIND → put directly in SPD
    /// 2. Extract VS from SIND → put as X-VS in SPD
    /// 3. Build reduced SIND string from remaining attributes
    /// 4. URL-encode the reduced SIND (replace * with %2A)
    /// 5. Put encoded SIND as X-INV value in SPD
    /// </summary>
    /// <param name="sindAttributes">All SIND attributes from SindBuilder.GetAttributes()</param>
    /// <returns>Complete SPD string ready for QR code encoding</returns>
    public static string BuildSpdWithInvoice(IReadOnlyDictionary<string, string> sindAttributes)
    {
        var spdAttributes = new Dictionary<string, string>();
        var remainingSindAttributes = new Dictionary<string, string>();

        foreach (var kvp in sindAttributes)
        {
            if (SharedKeys.Contains(kvp.Key))
            {
                // Shared key: move to SPD directly
                spdAttributes[kvp.Key] = kvp.Value;
            }
            else if (kvp.Key.Equals("VS", StringComparison.OrdinalIgnoreCase))
            {
                // VS in SIND becomes X-VS in SPD (special rule per specification)
                spdAttributes["X-VS"] = kvp.Value;
            }
            else
            {
                // Everything else stays in the reduced SIND string
                remainingSindAttributes[kvp.Key] = kvp.Value;
            }
        }

        // Build the reduced SIND string (without shared keys and VS)
        var reducedSind = BuildReducedSind(remainingSindAttributes);

        // URL-encode the reduced SIND: replace * with %2A
        var encodedSind = reducedSind.Replace("*", "%2A");

        // Add the encoded SIND as X-INV in SPD
        spdAttributes["X-INV"] = encodedSind;

        // Build the final SPD string
        return BuildSpdString(spdAttributes);
    }

    /// <summary>
    /// Builds a reduced SIND string from the remaining (non-shared) attributes.
    /// Format: SID*1.0*{KEY}:{VALUE}*{KEY}:{VALUE}*...
    /// Attributes are sorted alphabetically by key (canonical form).
    /// </summary>
    private static string BuildReducedSind(Dictionary<string, string> attributes)
    {
        var sb = new StringBuilder("SID*1.0*");

        foreach (var kvp in attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key).Append(':').Append(kvp.Value).Append('*');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the SPD (Short Payment Descriptor) string.
    /// Format: SPD*1.0*{KEY}:{VALUE}*{KEY}:{VALUE}
    /// Attributes are sorted alphabetically by key.
    /// Per the official SPD spec, there is NO trailing * after the last attribute.
    /// </summary>
    private static string BuildSpdString(Dictionary<string, string> attributes)
    {
        // Join attributes with * separator — no trailing * (per SPD spec)
        var entries = attributes.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(kvp => $"{kvp.Key}:{kvp.Value}");
        return $"SPD*1.0*{string.Join("*", entries)}";
    }
}
