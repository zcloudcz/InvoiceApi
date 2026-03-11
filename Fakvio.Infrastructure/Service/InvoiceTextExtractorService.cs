using System.Globalization;
using System.Text.RegularExpressions;
using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Extracts structured invoice data from plain text using regex patterns.
/// Last resort fallback in the extraction pipeline: QR → AI → Regex.
///
/// Each regex pattern is designed for common Czech invoice formats.
/// The patterns handle variations in whitespace, colons, and formatting
/// that different accounting software produces.
///
/// Pattern design principles:
/// - Case-insensitive matching (invoices use mixed casing)
/// - Optional colons and whitespace between label and value
/// - Named capture groups for clarity
/// - Each pattern tested independently
/// </summary>
public partial class InvoiceTextExtractorService : IInvoiceTextExtractor
{
    /// <summary>
    /// Extracts all recognizable invoice fields from the provided text.
    /// Fields that cannot be matched by regex remain null.
    /// </summary>
    public InvoiceExtractedData Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new InvoiceExtractedData { Source = EExtractionSource.RegexFallback };
        }

        var data = new InvoiceExtractedData
        {
            Source = EExtractionSource.RegexFallback,

            // Document identification
            DocumentNumber = ExtractDocumentNumber(text),
            IssueDate = ExtractDate(text, IssueDateRegex()),
            DueDate = ExtractDate(text, DueDateRegex()),
            TaxableSupplyDate = ExtractDate(text, DuzpRegex()),

            // Amounts
            TotalAmount = ExtractAmount(text, TotalAmountRegex()),
            TotalBeforeVat = ExtractAmount(text, TotalBeforeVatRegex()),
            TotalVat = ExtractAmount(text, TotalVatRegex()),

            // Payment details
            VariableSymbol = ExtractMatch(text, VariableSymbolRegex()),
            IBAN = ExtractMatch(text, IbanRegex()),
            BankAccountNumber = ExtractMatch(text, CzechBankAccountRegex()),

            // Identification numbers — we extract all occurrences and assign heuristically
            Currency = ExtractCurrency(text)
        };

        // Extract IČO and DIČ — there may be multiple (issuer + recipient)
        ExtractIdentificationNumbers(text, data);

        return data;
    }

    // ─── Regex patterns (source-generated for performance) ───────────────

    // Document number: "Faktura č. FV2026001" or "Číslo faktury: 20260042" or "Invoice No.: INV-001"
    [GeneratedRegex(@"(?:(?:faktura|invoice)\s*(?:č(?:íslo)?\.?|no\.?)|číslo\s+faktury)\s*:?\s*(?<value>[\w\-/]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentNumberRegex();

    // Issue date: "Datum vystavení: 01.03.2026" or "Date of issue: 2026-03-01"
    [GeneratedRegex(@"(?:datum\s+vystavení|date\s+of\s+issue|vystaveno)\s*:?\s*(?<value>\d{1,2}\.\s*\d{1,2}\.\s*\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex IssueDateRegex();

    // Due date: "Datum splatnosti: 15.03.2026" or "Due date: 2026-03-15"
    [GeneratedRegex(@"(?:datum\s+splatnosti|due\s+date|splatnost)\s*:?\s*(?<value>\d{1,2}\.\s*\d{1,2}\.\s*\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex DueDateRegex();

    // DUZP: "DUZP: 01.03.2026" or "Datum uskutečnění zdanitelného plnění: ..."
    [GeneratedRegex(@"(?:DUZP|[Dd]atum\s+uskut(?:ečnění)?\s+zdanit(?:elného)?\s+plnění)\s*:?\s*(?<value>\d{1,2}\.\s*\d{1,2}\.\s*\d{4}|\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex DuzpRegex();

    // Total amount: "Celkem k úhradě: 12 100,00 Kč" or "Total: 12100.00"
    // Uses negative lookbehind to avoid matching "DPH celkem" or "základ daně celkem"
    [GeneratedRegex(@"(?:celkem\s+k\s*úhradě|k\s*úhradě|(?<!DPH\s)(?<!daně\s)celkem|total\s*(?:amount)?)\s*:?\s*(?<value>[\d\s,\.]+)\s*(?:Kč|CZK|EUR|USD)?", RegexOptions.IgnoreCase)]
    private static partial Regex TotalAmountRegex();

    // Total before VAT: "Základ daně: 10 000,00" or "Subtotal: 10000.00"
    [GeneratedRegex(@"(?:základ\s*(?:daně)?|celkem\s+bez\s+DPH|subtotal|tax\s+base)\s*:?\s*(?<value>[\d\s,\.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalBeforeVatRegex();

    // Total VAT: "DPH celkem: 2 100,00" or "VAT: 2100.00"
    [GeneratedRegex(@"(?:DPH\s*(?:celkem)?|VAT\s*(?:total)?|daň\s*(?:celkem)?)\s*:?\s*(?<value>[\d\s,\.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalVatRegex();

    // Variable symbol: "Variabilní symbol: 2026001" or "VS: 2026001"
    [GeneratedRegex(@"(?:variabilní\s+symbol|VS)\s*:?\s*(?<value>\d{1,10})", RegexOptions.IgnoreCase)]
    private static partial Regex VariableSymbolRegex();

    // IBAN: standard format CZ + 22 alphanumeric characters (with optional spaces)
    [GeneratedRegex(@"(?<value>[A-Z]{2}\d{2}\s?\d{4}\s?\d{4}\s?\d{4}\s?\d{4}\s?\d{0,4})", RegexOptions.IgnoreCase)]
    private static partial Regex IbanRegex();

    // Czech bank account: "123456-1234567890/0100" or "1234567890/0100"
    [GeneratedRegex(@"(?<value>(?:\d{1,6}-)?\d{2,10}/\d{4})")]
    private static partial Regex CzechBankAccountRegex();

    // IČO (registration number): "IČ: 12345678" or "IČO: 12345678" or "Reg. No.: 12345678"
    [GeneratedRegex(@"(?:IČ[O]?\s*:?\s*|[Rr]eg\.?\s*[Nn]o\.?\s*:?\s*)(?<value>\d{8})", RegexOptions.IgnoreCase)]
    private static partial Regex RegistrationNumberRegex();

    // DIČ (tax number): "DIČ: CZ12345678" or "VAT ID: CZ12345678"
    [GeneratedRegex(@"(?:DIČ|VAT\s*ID|Tax\s*ID)\s*:?\s*(?<value>[A-Z]{2}\d{8,10})", RegexOptions.IgnoreCase)]
    private static partial Regex TaxNumberRegex();

    // Currency detection from text context
    [GeneratedRegex(@"(?:Kč|CZK|EUR|USD|GBP)", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyRegex();

    // ─── Extraction helpers ──────────────────────────────────────────────

    /// <summary>
    /// Extracts the document number using the regex pattern.
    /// Falls back to looking for common prefixes if the labeled pattern doesn't match.
    /// </summary>
    private static string? ExtractDocumentNumber(string text)
    {
        var match = DocumentNumberRegex().Match(text);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    /// <summary>
    /// Extracts a date using the provided regex pattern.
    /// Handles both Czech format (DD.MM.YYYY) and ISO format (YYYY-MM-DD).
    /// </summary>
    private static DateTime? ExtractDate(string text, Regex regex)
    {
        var match = regex.Match(text);
        if (!match.Success) return null;

        var dateStr = match.Groups["value"].Value.Trim();
        // Remove spaces within date (e.g., "01. 03. 2026" → "01.03.2026")
        dateStr = dateStr.Replace(" ", "");

        string[] formats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd"];
        return DateTime.TryParseExact(dateStr, formats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// Extracts a monetary amount from text.
    /// Handles Czech formatting: spaces as thousands separator, comma as decimal separator.
    /// Examples: "12 100,00" → 12100.00, "12100.00" → 12100.00
    /// </summary>
    private static decimal? ExtractAmount(string text, Regex regex)
    {
        var match = regex.Match(text);
        if (!match.Success) return null;

        var amountStr = match.Groups["value"].Value.Trim();
        // Remove spaces (thousands separator in Czech format)
        amountStr = amountStr.Replace(" ", "");
        // Replace comma with dot (Czech decimal separator)
        amountStr = amountStr.Replace(",", ".");

        return decimal.TryParse(amountStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;
    }

    /// <summary>
    /// Extracts a simple string value from a regex match.
    /// </summary>
    private static string? ExtractMatch(string text, Regex regex)
    {
        var match = regex.Match(text);
        if (!match.Success) return null;

        var value = match.Groups["value"].Value.Trim();
        // Clean up IBAN — remove spaces
        if (regex == IbanRegex())
        {
            value = value.Replace(" ", "");
        }
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Detects currency from text context.
    /// Returns the ISO 4217 code (CZK for "Kč").
    /// </summary>
    private static string? ExtractCurrency(string text)
    {
        var match = CurrencyRegex().Match(text);
        if (!match.Success) return null;

        var currency = match.Value.Trim().ToUpperInvariant();
        return currency switch
        {
            "KČ" => "CZK",
            _ => currency
        };
    }

    /// <summary>
    /// Extracts IČO and DIČ numbers from text.
    ///
    /// Challenge: an invoice has TWO IČO/DIČ pairs (issuer + recipient).
    /// We use a heuristic: the FIRST occurrence is typically the issuer
    /// (invoices usually list the issuer at the top, recipient below).
    ///
    /// If two IČO numbers are found, we assign:
    /// - First = issuer (IssuerRegistrationNumber)
    /// - Second = recipient (RecipientRegistrationNumber)
    /// </summary>
    private static void ExtractIdentificationNumbers(string text, InvoiceExtractedData data)
    {
        // Extract all IČO matches
        var icoMatches = RegistrationNumberRegex().Matches(text);
        if (icoMatches.Count >= 1)
        {
            data.IssuerRegistrationNumber = icoMatches[0].Groups["value"].Value;
        }
        if (icoMatches.Count >= 2)
        {
            data.RecipientRegistrationNumber = icoMatches[1].Groups["value"].Value;
        }

        // Extract all DIČ matches
        var dicMatches = TaxNumberRegex().Matches(text);
        if (dicMatches.Count >= 1)
        {
            data.IssuerTaxNumber = dicMatches[0].Groups["value"].Value;
        }
        if (dicMatches.Count >= 2)
        {
            data.RecipientTaxNumber = dicMatches[1].Groups["value"].Value;
        }
    }
}
