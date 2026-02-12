using System.Globalization;
using System.Text;

namespace InvoiceApi.Application.QrPayment;

/// <summary>
/// Builds the SIND (Short Invoice Descriptor) string for the QR Faktura standard.
///
/// SIND is a compact text format encoding invoice header data into a QR code.
/// Format: SID*1.0*{KEY}:{VALUE}*{KEY}:{VALUE}*...
///
/// Required attributes: ID (document number), DD (issue date), AM (total amount).
/// Optional attributes: VS, ACC, CC, DT, VII, INI, VIR, INR, DUZP, TB0, T0, TB1, T1, NTB, MSG, etc.
///
/// Reference: https://www.kdpcr.cz/informace/qr-faktura/popis-formatu
/// </summary>
public class SindBuilder
{
    // Stores all SIND attributes as key-value pairs.
    // Keys are always uppercase (e.g., "ID", "DD", "AM").
    private readonly Dictionary<string, string> _attributes = new();

    /// <summary>
    /// Sets a SIND attribute. Overwrites if the key already exists.
    /// Null or whitespace values are silently ignored (attribute not added).
    /// </summary>
    /// <param name="key">Attribute key (e.g., "ID", "DD", "AM")</param>
    /// <param name="value">Attribute value</param>
    /// <returns>This builder for method chaining</returns>
    public SindBuilder Set(string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _attributes[key.ToUpperInvariant()] = value;
        }
        return this;
    }

    /// <summary>
    /// Sets the document ID (required). Max 40 chars.
    /// Usually the invoice/document number.
    /// </summary>
    public SindBuilder SetDocumentId(string documentNumber) => Set("ID", documentNumber);

    /// <summary>
    /// Sets the issue date (required). Format: YYYYMMDD.
    /// </summary>
    public SindBuilder SetIssueDate(DateTime date) => Set("DD", date.ToString("yyyyMMdd"));

    /// <summary>
    /// Sets the total amount (required). Max 18 chars, max 2 decimal places, dot separator.
    /// </summary>
    public SindBuilder SetAmount(decimal amount) =>
        Set("AM", amount.ToString("F2", CultureInfo.InvariantCulture));

    /// <summary>
    /// Sets the variable symbol (VS). Max 10 digits.
    /// </summary>
    public SindBuilder SetVariableSymbol(string? vs) => Set("VS", vs);

    /// <summary>
    /// Sets the bank account (ACC). Format: IBAN or IBAN+BIC.
    /// Example: "CZ5855000000001265098001+RZBCCZPP"
    /// </summary>
    public SindBuilder SetAccount(string? iban, string? swift = null)
    {
        if (string.IsNullOrWhiteSpace(iban)) return this;
        // Remove spaces and dashes from IBAN (users often input formatted IBANs like "CZ58 5500 ...")
        var cleanIban = iban.Replace(" ", "").Replace("-", "");
        var acc = !string.IsNullOrWhiteSpace(swift) ? $"{cleanIban}+{swift}" : cleanIban;
        return Set("ACC", acc);
    }

    /// <summary>
    /// Sets the currency code (CC). ISO 4217, 3 uppercase letters. Default: CZK.
    /// </summary>
    public SindBuilder SetCurrency(string? currencyCode) => Set("CC", currencyCode?.ToUpperInvariant());

    /// <summary>
    /// Sets the due date (DT). Format: YYYYMMDD.
    /// </summary>
    public SindBuilder SetDueDate(DateTime? date)
    {
        if (date.HasValue) Set("DT", date.Value.ToString("yyyyMMdd"));
        return this;
    }

    /// <summary>
    /// Sets the issuer's tax number (VII = VAT ID Issuer). Max 14 chars.
    /// Example: "CZ12345678"
    /// </summary>
    public SindBuilder SetIssuerTaxNumber(string? taxNumber) => Set("VII", taxNumber);

    /// <summary>
    /// Sets the issuer's registration number (INI = IČO). Max 8 digits.
    /// </summary>
    public SindBuilder SetIssuerRegistrationNumber(string? regNumber) => Set("INI", regNumber);

    /// <summary>
    /// Sets the recipient's tax number (VIR = VAT ID Recipient). Max 14 chars.
    /// </summary>
    public SindBuilder SetRecipientTaxNumber(string? taxNumber) => Set("VIR", taxNumber);

    /// <summary>
    /// Sets the recipient's registration number (INR = IČO). Max 8 digits.
    /// </summary>
    public SindBuilder SetRecipientRegistrationNumber(string? regNumber) => Set("INR", regNumber);

    /// <summary>
    /// Sets the date of taxable supply (DUZP). Format: YYYYMMDD.
    /// </summary>
    public SindBuilder SetTaxableSupplyDate(DateTime? date)
    {
        if (date.HasValue) Set("DUZP", date.Value.ToString("yyyyMMdd"));
        return this;
    }

    /// <summary>
    /// Sets VAT breakdown for the standard (basic) rate.
    /// TB0 = tax base, T0 = tax amount.
    /// </summary>
    public SindBuilder SetStandardVat(decimal? taxBase, decimal? tax)
    {
        if (taxBase.HasValue && taxBase.Value != 0)
            Set("TB0", taxBase.Value.ToString("F2", CultureInfo.InvariantCulture));
        if (tax.HasValue && tax.Value != 0)
            Set("T0", tax.Value.ToString("F2", CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>
    /// Sets VAT breakdown for the first reduced rate.
    /// TB1 = tax base, T1 = tax amount.
    /// </summary>
    public SindBuilder SetReducedVat1(decimal? taxBase, decimal? tax)
    {
        if (taxBase.HasValue && taxBase.Value != 0)
            Set("TB1", taxBase.Value.ToString("F2", CultureInfo.InvariantCulture));
        if (tax.HasValue && tax.Value != 0)
            Set("T1", tax.Value.ToString("F2", CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>
    /// Sets VAT breakdown for the second reduced rate.
    /// TB2 = tax base, T2 = tax amount.
    /// </summary>
    public SindBuilder SetReducedVat2(decimal? taxBase, decimal? tax)
    {
        if (taxBase.HasValue && taxBase.Value != 0)
            Set("TB2", taxBase.Value.ToString("F2", CultureInfo.InvariantCulture));
        if (tax.HasValue && tax.Value != 0)
            Set("T2", tax.Value.ToString("F2", CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>
    /// Sets the non-taxable amount (NTB) — for VAT-exempt or non-VAT-payer invoices.
    /// </summary>
    public SindBuilder SetNonTaxableAmount(decimal? amount)
    {
        if (amount.HasValue && amount.Value != 0)
            Set("NTB", amount.Value.ToString("F2", CultureInfo.InvariantCulture));
        return this;
    }

    /// <summary>
    /// Sets the document type (TD). Values: 0=advance, 1=corrective, 9=other (default).
    /// </summary>
    public SindBuilder SetDocumentType(int type) => Set("TD", type.ToString());

    /// <summary>
    /// Sets the software identifier (X-SW). Proprietary key, max 30 chars.
    /// </summary>
    public SindBuilder SetSoftware(string name) => Set("X-SW", name);

    /// <summary>
    /// Builds the SIND string WITHOUT CRC32.
    /// Used internally for CRC32 computation and for integration with SPD.
    /// </summary>
    public string BuildWithoutCrc()
    {
        var sb = new StringBuilder("SID*1.0*");

        // Attributes are appended in alphabetical order by key (canonical form)
        foreach (var kvp in _attributes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            sb.Append(kvp.Key).Append(':').Append(kvp.Value).Append('*');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds the complete SIND string WITH CRC32 checksum appended.
    /// This is the final format for standalone QR Faktura encoding.
    /// </summary>
    public string Build()
    {
        var sindWithoutCrc = BuildWithoutCrc();
        var crc = Crc32Calculator.Compute(sindWithoutCrc);
        // No trailing * after CRC32 — it is the final token in the SIND string
        return sindWithoutCrc + $"CRC32:{crc}";
    }

    /// <summary>
    /// Returns a copy of all attributes currently set.
    /// Used by SpdIntegrator to extract shared keys.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetAttributes() =>
        new Dictionary<string, string>(_attributes);
}
