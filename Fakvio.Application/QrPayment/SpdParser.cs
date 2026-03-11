using System.Globalization;

namespace Fakvio.Application.QrPayment;

/// <summary>
/// Parses an SPD (Short Payment Descriptor) string back into structured data.
/// This is the reverse operation of <see cref="SpdIntegrator"/>.
///
/// SPD format: SPD*1.0*{KEY}:{VALUE}*{KEY}:{VALUE}
///
/// SPD strings may optionally contain an embedded SIND invoice descriptor
/// in the X-INV attribute (URL-encoded). When present, the parser decodes
/// and delegates to <see cref="SindParser"/> for the invoice data.
///
/// Reference: https://qr-platba.cz/pro-vyvojare/specifikace-formatu/
/// </summary>
public static class SpdParser
{
    /// <summary>
    /// Required prefix for all SPD strings. Version 1.0 is the only supported version.
    /// </summary>
    private const string SpdPrefix = "SPD*1.0*";

    /// <summary>
    /// Parses an SPD string into a <see cref="SpdData"/> object.
    /// If the SPD contains an X-INV attribute, the embedded SIND is also parsed.
    /// Returns null if the string is not a valid SPD format.
    /// </summary>
    /// <param name="spdString">
    /// The raw SPD string, e.g.:
    /// "SPD*1.0*ACC:CZ5855000000001265098001+RZBCCZPP*AM:5850.00*CC:CZK*DT:20260315*X-VS:2026001"
    /// </param>
    /// <returns>Parsed SPD data (with optional embedded SIND), or null if parsing fails.</returns>
    public static SpdData? Parse(string? spdString)
    {
        // Guard: null or empty input
        if (string.IsNullOrWhiteSpace(spdString))
            return null;

        // Guard: must start with the SPD prefix (case-sensitive per spec)
        if (!spdString.StartsWith(SpdPrefix, StringComparison.Ordinal))
            return null;

        // Remove the prefix to get the attribute tokens
        var body = spdString[SpdPrefix.Length..];

        // Split on '*' delimiter — SPD has no trailing * (unlike SIND)
        var tokens = body.Split('*', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            return null;

        // Parse all key:value pairs into a dictionary
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            var colonIndex = token.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= token.Length - 1)
                continue;

            var key = token[..colonIndex];
            var value = token[(colonIndex + 1)..];
            attributes[key] = value;
        }

        // Build the SpdData object from parsed attributes
        var data = MapToSpdData(attributes);

        // Check for embedded SIND in X-INV attribute.
        // X-INV contains a URL-encoded reduced SIND string (per the integration spec).
        // Reduced SIND = SIND without shared keys (ACC, AM, CC, DT) which are already in SPD.
        if (attributes.TryGetValue("X-INV", out var encodedSind) &&
            !string.IsNullOrWhiteSpace(encodedSind))
        {
            // URL-decode: %2A → * (the main encoding used for SIND-in-SPD)
            var decodedSind = Uri.UnescapeDataString(encodedSind);

            // Parse the reduced SIND (without CRC validation — reduced SIND may not have CRC)
            data.EmbeddedSind = SindParser.Parse(decodedSind, validateCrc: false);

            // Merge shared keys back: the reduced SIND is missing ACC, AM, CC, DT
            // because those were extracted into SPD. Restore them for completeness.
            if (data.EmbeddedSind != null)
            {
                data.EmbeddedSind.Amount ??= data.Amount;
                data.EmbeddedSind.Currency ??= data.Currency;
                data.EmbeddedSind.DueDate ??= data.DueDate;
                data.EmbeddedSind.IBAN ??= data.IBAN;
                data.EmbeddedSind.SWIFT ??= data.SWIFT;

                // X-VS in SPD corresponds to VS in SIND
                if (!string.IsNullOrWhiteSpace(data.VariableSymbol))
                {
                    data.EmbeddedSind.VariableSymbol ??= data.VariableSymbol;
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Attempts to detect whether a raw QR code string is SPD format.
    /// Quick check without full parsing — useful for dispatching to the correct parser.
    /// </summary>
    public static bool IsSpdString(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.StartsWith(SpdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Maps a dictionary of SPD attributes to a typed <see cref="SpdData"/> object.
    /// </summary>
    private static SpdData MapToSpdData(Dictionary<string, string> attrs)
    {
        var data = new SpdData
        {
            Amount = GetDecimal(attrs, "AM"),
            Currency = GetString(attrs, "CC"),
            VariableSymbol = GetString(attrs, "X-VS"),
            Message = GetString(attrs, "MSG"),
            DueDate = GetDate(attrs, "DT"),
            RecipientName = GetString(attrs, "RN"),
            ConstantSymbol = GetString(attrs, "X-KS"),
            SpecificSymbol = GetString(attrs, "X-SS"),
            NotificationType = GetString(attrs, "NT"),
            NotificationAddress = GetString(attrs, "NTA"),
            RawAttributes = new Dictionary<string, string>(attrs, StringComparer.OrdinalIgnoreCase)
        };

        // Parse ACC (bank account) — format: "IBAN" or "IBAN+BIC"
        if (attrs.TryGetValue("ACC", out var acc) && !string.IsNullOrWhiteSpace(acc))
        {
            var plusIndex = acc.IndexOf('+');
            if (plusIndex > 0)
            {
                data.IBAN = acc[..plusIndex];
                data.SWIFT = acc[(plusIndex + 1)..];
            }
            else
            {
                data.IBAN = acc;
            }
        }

        // Parse alternative accounts (ACC1, ACC2, ...) — SPD supports multiple accounts
        for (int i = 1; i <= 2; i++)
        {
            if (attrs.TryGetValue($"ACC{i}", out var altAcc) && !string.IsNullOrWhiteSpace(altAcc))
            {
                data.AlternativeAccounts.Add(altAcc);
            }
        }

        return data;
    }

    // ─── Helper methods (same pattern as SindParser) ─────────────────────

    private static string? GetString(Dictionary<string, string> attrs, string key) =>
        attrs.TryGetValue(key, out var value) ? value : null;

    private static DateTime? GetDate(Dictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var value))
            return null;

        return DateTime.TryParseExact(value, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static decimal? GetDecimal(Dictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var value))
            return null;

        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}

/// <summary>
/// Typed representation of parsed SPD (Short Payment Descriptor) data.
/// SPD is the QR Platba standard used by Czech banking apps.
///
/// Unlike SIND (invoice data), SPD focuses on payment information:
/// bank account, amount, currency, due date, variable symbol.
///
/// May optionally contain an embedded <see cref="SindData"/> via the X-INV attribute.
/// </summary>
public class SpdData
{
    // ─── Payment info ────────────────────────────────────────────────────

    /// <summary>IBAN of the payee — parsed from ACC attribute.</summary>
    public string? IBAN { get; set; }

    /// <summary>SWIFT/BIC code — parsed from ACC attribute (after +).</summary>
    public string? SWIFT { get; set; }

    /// <summary>Payment amount (SPD: AM).</summary>
    public decimal? Amount { get; set; }

    /// <summary>ISO 4217 currency code (SPD: CC). Default: CZK.</summary>
    public string? Currency { get; set; }

    /// <summary>Payment due date (SPD: DT).</summary>
    public DateTime? DueDate { get; set; }

    /// <summary>Variable symbol (SPD: X-VS) — Czech payment identifier.</summary>
    public string? VariableSymbol { get; set; }

    /// <summary>Constant symbol (SPD: X-KS).</summary>
    public string? ConstantSymbol { get; set; }

    /// <summary>Specific symbol (SPD: X-SS).</summary>
    public string? SpecificSymbol { get; set; }

    /// <summary>Payment message / reference (SPD: MSG). Max 60 chars.</summary>
    public string? Message { get; set; }

    /// <summary>Recipient name (SPD: RN). Max 35 chars.</summary>
    public string? RecipientName { get; set; }

    /// <summary>Notification type: "P" for phone, "E" for email (SPD: NT).</summary>
    public string? NotificationType { get; set; }

    /// <summary>Notification address — phone or email (SPD: NTA).</summary>
    public string? NotificationAddress { get; set; }

    /// <summary>Alternative bank accounts (SPD: ACC1, ACC2).</summary>
    public List<string> AlternativeAccounts { get; set; } = new();

    // ─── Embedded invoice data ───────────────────────────────────────────

    /// <summary>
    /// Parsed SIND data from the X-INV attribute, if present.
    /// Null when the SPD is a pure payment QR without invoice data.
    /// When present, shared fields (ACC, AM, CC, DT) are merged back into SIND.
    /// </summary>
    public SindData? EmbeddedSind { get; set; }

    // ─── Raw data ────────────────────────────────────────────────────────

    /// <summary>All raw key-value pairs from the SPD string.</summary>
    public Dictionary<string, string> RawAttributes { get; set; } = new();

    /// <summary>
    /// Converts this SPD data to the unified <see cref="InvoiceExtractedData"/> format.
    /// If embedded SIND is present, its data takes priority (more detailed).
    /// Otherwise, only payment fields are populated.
    /// </summary>
    public InvoiceExtractedData ToExtractedData()
    {
        // If we have embedded SIND, use it as the base (it has more invoice-specific fields)
        if (EmbeddedSind != null)
        {
            return EmbeddedSind.ToExtractedData();
        }

        // Pure SPD — only payment fields are available
        return new InvoiceExtractedData
        {
            TotalAmount = Amount,
            Currency = Currency,
            DueDate = DueDate,
            VariableSymbol = VariableSymbol,
            IBAN = IBAN,
            SWIFT = SWIFT,
            Source = EExtractionSource.QrCode
        };
    }
}
