using System.Globalization;

namespace Fakvio.Application.QrPayment;

/// <summary>
/// Parses a SIND (Short Invoice Descriptor) string back into structured data.
/// This is the reverse operation of <see cref="SindBuilder"/>.
///
/// SIND format: SID*1.0*{KEY}:{VALUE}*{KEY}:{VALUE}*...*CRC32:{checksum}
///
/// The parser:
/// 1. Validates the "SID*1.0*" prefix
/// 2. Splits on '*' delimiter to get key:value tokens
/// 3. Extracts and validates the CRC32 checksum
/// 4. Maps known attributes to typed properties in <see cref="SindData"/>
///
/// Reference: https://www.kdpcr.cz/informace/qr-faktura/popis-formatu
/// </summary>
public static class SindParser
{
    /// <summary>
    /// Required prefix for all SIND strings. Version 1.0 is the only supported version.
    /// </summary>
    private const string SindPrefix = "SID*1.0*";

    /// <summary>
    /// Parses a SIND string into a <see cref="SindData"/> object.
    /// Returns null if the string is not a valid SIND format.
    /// </summary>
    /// <param name="sindString">
    /// The raw SIND string, e.g.:
    /// "SID*1.0*AM:5850.00*CC:CZK*DD:20260301*ID:FV2026001*VS:2026001*CRC32:1234ABCD"
    /// </param>
    /// <param name="validateCrc">
    /// Whether to validate the CRC32 checksum. Default: true.
    /// Set to false for testing or when CRC validation is not needed.
    /// </param>
    /// <returns>Parsed SIND data, or null if parsing fails.</returns>
    public static SindData? Parse(string? sindString, bool validateCrc = true)
    {
        // Guard: null or empty input
        if (string.IsNullOrWhiteSpace(sindString))
            return null;

        // Guard: must start with the SIND prefix (case-sensitive per spec)
        if (!sindString.StartsWith(SindPrefix, StringComparison.Ordinal))
            return null;

        // Remove the prefix to get the attribute tokens
        var body = sindString[SindPrefix.Length..];

        // Split on '*' delimiter — each token is "KEY:VALUE" or just empty (trailing *)
        var tokens = body.Split('*', StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
            return null;

        // Parse all key:value pairs into a dictionary
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            // Each token must contain exactly one ':' separating key from value
            var colonIndex = token.IndexOf(':');
            if (colonIndex <= 0 || colonIndex >= token.Length - 1)
                continue; // Skip malformed tokens (no key, no value, or no colon)

            var key = token[..colonIndex].ToUpperInvariant();
            var value = token[(colonIndex + 1)..];
            attributes[key] = value;
        }

        // CRC32 validation: compute CRC from the string WITHOUT the CRC32 token
        if (validateCrc)
        {
            if (!attributes.TryGetValue("CRC32", out var expectedCrc))
                return null; // CRC32 is mandatory when validation is enabled

            // Rebuild the SIND string without CRC32 for checksum computation.
            // This must match exactly what SindBuilder.BuildWithoutCrc() produces:
            // "SID*1.0*{sorted key:value pairs each ending with *}"
            var attrsWithoutCrc = attributes
                .Where(a => !a.Key.Equals("CRC32", StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Key, StringComparer.Ordinal);

            var sindWithoutCrc = SindPrefix +
                string.Join("", attrsWithoutCrc.Select(a => $"{a.Key}:{a.Value}*"));

            var computedCrc = Crc32Calculator.Compute(sindWithoutCrc);

            if (!computedCrc.Equals(expectedCrc, StringComparison.OrdinalIgnoreCase))
                return null; // CRC mismatch — data may be corrupted
        }

        // Map dictionary values to typed SindData properties
        return MapToSindData(attributes);
    }

    /// <summary>
    /// Attempts to detect whether a raw QR code string is SIND format.
    /// Quick check without full parsing — useful for dispatching to the correct parser.
    /// </summary>
    public static bool IsSindString(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        text.StartsWith(SindPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Maps a dictionary of SIND attributes to a typed <see cref="SindData"/> object.
    /// Unknown attributes are silently ignored (forward compatibility).
    /// </summary>
    private static SindData MapToSindData(Dictionary<string, string> attrs)
    {
        var data = new SindData
        {
            // Required fields
            DocumentNumber = GetString(attrs, "ID"),
            IssueDate = GetDate(attrs, "DD"),
            Amount = GetDecimal(attrs, "AM"),

            // Payment info
            VariableSymbol = GetString(attrs, "VS"),
            Currency = GetString(attrs, "CC"),
            DueDate = GetDate(attrs, "DT"),

            // Issuer identification
            IssuerTaxNumber = GetString(attrs, "VII"),
            IssuerRegistrationNumber = GetString(attrs, "INI"),

            // Recipient identification
            RecipientTaxNumber = GetString(attrs, "VIR"),
            RecipientRegistrationNumber = GetString(attrs, "INR"),

            // Tax dates
            TaxableSupplyDate = GetDate(attrs, "DUZP"),

            // VAT breakdown
            StandardVatBase = GetDecimal(attrs, "TB0"),
            StandardVatAmount = GetDecimal(attrs, "T0"),
            ReducedVat1Base = GetDecimal(attrs, "TB1"),
            ReducedVat1Amount = GetDecimal(attrs, "T1"),
            ReducedVat2Base = GetDecimal(attrs, "TB2"),
            ReducedVat2Amount = GetDecimal(attrs, "T2"),
            NonTaxableAmount = GetDecimal(attrs, "NTB"),

            // Document type
            DocumentType = GetInt(attrs, "TD"),

            // Software identifier
            Software = GetString(attrs, "X-SW"),

            // Store all raw attributes for potential future use
            RawAttributes = new Dictionary<string, string>(attrs, StringComparer.OrdinalIgnoreCase)
        };

        // Parse IBAN and SWIFT from ACC attribute.
        // ACC format: "IBAN" or "IBAN+BIC"
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

        return data;
    }

    // ─── Helper methods for type-safe attribute extraction ───────────────

    /// <summary>
    /// Gets a string value from the dictionary, or null if the key doesn't exist.
    /// </summary>
    private static string? GetString(Dictionary<string, string> attrs, string key) =>
        attrs.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// Parses a date from YYYYMMDD format. Returns null if parsing fails.
    /// SIND dates are always in this fixed format (no separators).
    /// </summary>
    private static DateTime? GetDate(Dictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var value))
            return null;

        return DateTime.TryParseExact(value, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// Parses a decimal value with invariant culture (dot as decimal separator).
    /// Returns null if parsing fails. SIND amounts use max 2 decimal places.
    /// </summary>
    private static decimal? GetDecimal(Dictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var value))
            return null;

        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    /// <summary>
    /// Parses an integer value. Returns null if parsing fails.
    /// Used for document type (TD attribute).
    /// </summary>
    private static int? GetInt(Dictionary<string, string> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var value))
            return null;

        return int.TryParse(value, out var result) ? result : null;
    }
}

/// <summary>
/// Typed representation of parsed SIND (Short Invoice Descriptor) data.
/// All fields are nullable because not all SIND strings contain all attributes.
///
/// The required fields per the SIND spec are: ID (DocumentNumber), DD (IssueDate), AM (Amount).
/// Everything else is optional.
/// </summary>
public class SindData
{
    // ─── Required fields ─────────────────────────────────────────────────

    /// <summary>Document number (SIND: ID). Max 40 chars.</summary>
    public string? DocumentNumber { get; set; }

    /// <summary>Issue date (SIND: DD). Format: YYYYMMDD.</summary>
    public DateTime? IssueDate { get; set; }

    /// <summary>Total amount including VAT (SIND: AM). Max 2 decimal places.</summary>
    public decimal? Amount { get; set; }

    // ─── Payment info ────────────────────────────────────────────────────

    /// <summary>Variable symbol (SIND: VS). Max 10 digits.</summary>
    public string? VariableSymbol { get; set; }

    /// <summary>IBAN — parsed from ACC attribute (before + separator).</summary>
    public string? IBAN { get; set; }

    /// <summary>SWIFT/BIC — parsed from ACC attribute (after + separator).</summary>
    public string? SWIFT { get; set; }

    /// <summary>ISO 4217 currency code (SIND: CC). Default: CZK.</summary>
    public string? Currency { get; set; }

    /// <summary>Payment due date (SIND: DT).</summary>
    public DateTime? DueDate { get; set; }

    // ─── Issuer identification ───────────────────────────────────────────

    /// <summary>Issuer's VAT ID / tax number (SIND: VII). E.g., "CZ12345678".</summary>
    public string? IssuerTaxNumber { get; set; }

    /// <summary>Issuer's registration number / IČO (SIND: INI). 8 digits.</summary>
    public string? IssuerRegistrationNumber { get; set; }

    // ─── Recipient identification ────────────────────────────────────────

    /// <summary>Recipient's VAT ID / tax number (SIND: VIR).</summary>
    public string? RecipientTaxNumber { get; set; }

    /// <summary>Recipient's registration number / IČO (SIND: INR).</summary>
    public string? RecipientRegistrationNumber { get; set; }

    // ─── Tax dates ───────────────────────────────────────────────────────

    /// <summary>Date of taxable supply / DUZP (SIND: DUZP).</summary>
    public DateTime? TaxableSupplyDate { get; set; }

    // ─── VAT breakdown ───────────────────────────────────────────────────

    /// <summary>Tax base for standard (basic) VAT rate (SIND: TB0).</summary>
    public decimal? StandardVatBase { get; set; }
    /// <summary>Tax amount for standard VAT rate (SIND: T0).</summary>
    public decimal? StandardVatAmount { get; set; }

    /// <summary>Tax base for first reduced VAT rate (SIND: TB1).</summary>
    public decimal? ReducedVat1Base { get; set; }
    /// <summary>Tax amount for first reduced VAT rate (SIND: T1).</summary>
    public decimal? ReducedVat1Amount { get; set; }

    /// <summary>Tax base for second reduced VAT rate (SIND: TB2).</summary>
    public decimal? ReducedVat2Base { get; set; }
    /// <summary>Tax amount for second reduced VAT rate (SIND: T2).</summary>
    public decimal? ReducedVat2Amount { get; set; }

    /// <summary>Non-taxable amount (SIND: NTB). For VAT-exempt items.</summary>
    public decimal? NonTaxableAmount { get; set; }

    // ─── Metadata ────────────────────────────────────────────────────────

    /// <summary>Document type (SIND: TD). 0=advance, 1=corrective, 9=other.</summary>
    public int? DocumentType { get; set; }

    /// <summary>Software identifier (SIND: X-SW).</summary>
    public string? Software { get; set; }

    /// <summary>
    /// All raw key-value pairs from the SIND string.
    /// Useful for accessing non-standard or future attributes.
    /// </summary>
    public Dictionary<string, string> RawAttributes { get; set; } = new();

    /// <summary>
    /// Converts this SIND data to the unified <see cref="InvoiceExtractedData"/> format.
    /// Maps SIND-specific fields to the common invoice data model.
    /// </summary>
    public InvoiceExtractedData ToExtractedData()
    {
        // Compute TotalBeforeVat from VAT breakdown if available
        decimal? totalBeforeVat = null;
        var bases = new[] { StandardVatBase, ReducedVat1Base, ReducedVat2Base, NonTaxableAmount };
        if (bases.Any(b => b.HasValue))
        {
            totalBeforeVat = bases.Where(b => b.HasValue).Sum(b => b!.Value);
        }

        // Compute TotalVat from VAT breakdown if available
        decimal? totalVat = null;
        var taxes = new[] { StandardVatAmount, ReducedVat1Amount, ReducedVat2Amount };
        if (taxes.Any(t => t.HasValue))
        {
            totalVat = taxes.Where(t => t.HasValue).Sum(t => t!.Value);
        }

        return new InvoiceExtractedData
        {
            DocumentNumber = DocumentNumber,
            IssueDate = IssueDate,
            DueDate = DueDate,
            TaxableSupplyDate = TaxableSupplyDate,
            TotalAmount = Amount,
            TotalVat = totalVat,
            TotalBeforeVat = totalBeforeVat,
            Currency = Currency,
            VariableSymbol = VariableSymbol,
            IBAN = IBAN,
            SWIFT = SWIFT,
            IssuerRegistrationNumber = IssuerRegistrationNumber,
            IssuerTaxNumber = IssuerTaxNumber,
            RecipientRegistrationNumber = RecipientRegistrationNumber,
            RecipientTaxNumber = RecipientTaxNumber,
            StandardVatBase = StandardVatBase,
            StandardVatAmount = StandardVatAmount,
            ReducedVat1Base = ReducedVat1Base,
            ReducedVat1Amount = ReducedVat1Amount,
            ReducedVat2Base = ReducedVat2Base,
            ReducedVat2Amount = ReducedVat2Amount,
            NonTaxableAmount = NonTaxableAmount,
            Source = EExtractionSource.QrCode
        };
    }
}
