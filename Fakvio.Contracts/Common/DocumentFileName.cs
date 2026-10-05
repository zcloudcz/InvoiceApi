using System.Text.RegularExpressions;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Common;

/// <summary>
/// The values a file name format can refer to via <c>{{Placeholder}}</c> keys.
/// Plain data holder so <see cref="DocumentFileName.Build"/> stays a pure function
/// (no database, no clock) and is trivial to unit test.
/// </summary>
/// <param name="DocumentType">Invoice / credit note / proforma / tax receipt.</param>
/// <param name="Language">Client language ("cs", "en"). Decides the wording of <c>{{DocumentType}}</c>.</param>
/// <param name="InvoiceId">Used instead of the document number while the document is still a draft.</param>
/// <param name="DocumentNumber">Null for drafts.</param>
/// <param name="VariableSymbol">Payment variable symbol.</param>
/// <param name="IssueDate">Used for <c>{{IssueDate}}</c>, <c>{{Year}}</c> and <c>{{Month}}</c>.</param>
/// <param name="ClientName">Customer company name.</param>
/// <param name="IssuerName">Our own company name.</param>
public sealed record DocumentFileNameValues(
    EDocumentType DocumentType,
    string? Language,
    long InvoiceId,
    string? DocumentNumber,
    string? VariableSymbol,
    DateTime? IssueDate,
    string? ClientName,
    string? IssuerName);

/// <summary>
/// THE single place where file names of generated documents (PDF, ISDOC, UBL) are built.
/// Download endpoints, e-mail attachments, reminders, cloud upload, chat and MCP all call
/// this — never compose a document file name anywhere else, or the names drift apart again.
///
/// The format is configured per client and per document type (BillingSettings.*FileNameFormat),
/// with the issuer's setting as company-wide default and <see cref="DefaultFormat"/> as the last resort.
/// A format uses <c>{{Key}}</c> placeholders (same syntax as content templates) and never contains
/// the file extension — the extension is appended here.
/// </summary>
public static class DocumentFileName
{
    /// <summary>Format used when neither the client nor the issuer configured one.</summary>
    public const string DefaultFormat = "{{DocumentType}}_{{DocumentNumber}}";

    /// <summary>File extensions passed to <see cref="Build"/>. ISDOC and UBL share the PDF format.</summary>
    public const string PdfExtension = ".pdf";
    public const string IsdocExtension = ".isdoc";
    public const string UblExtension = ".xml";

    /// <summary>Longest allowed file name before the extension (keeps full paths below Windows' 260 limit).</summary>
    public const int MaxLength = 150;

    /// <summary>Allowed length of a stored format (matches the DB column).</summary>
    public const int MaxFormatLength = 200;

    /// <summary>All recognised placeholder keys. Also used by the UI (FileNameFormatInput) for the live preview.</summary>
    public static readonly IReadOnlyList<string> PlaceholderKeys =
    [
        "DocumentType", "DocumentNumber", "VariableSymbol", "IssueDate",
        "Year", "Month", "ClientName", "IssuerName"
    ];

    // Same shape as ContentTemplateService: {{Key}}. Whitespace inside the braces is tolerated.
    private static readonly Regex PlaceholderRegex =
        new(@"\{\{\s*(.+?)\s*\}\}", RegexOptions.Compiled);

    // Characters Windows refuses in file names (Linux would accept most of them, but users
    // download the files to Windows). Control characters are handled separately below.
    private static readonly char[] InvalidChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Builds the final file name, e.g. <c>Faktura_2026001.pdf</c>.
    /// </summary>
    /// <param name="format">Configured format, or null/blank to use <see cref="DefaultFormat"/>.</param>
    /// <param name="values">Document data the placeholders resolve to.</param>
    /// <param name="extension">Extension including the dot (see the *Extension constants).</param>
    public static string Build(string? format, DocumentFileNameValues values, string extension)
    {
        var name = Render(string.IsNullOrWhiteSpace(format) ? DefaultFormat : format, values);

        // A format made only of unknown/empty placeholders would yield an empty name —
        // fall back to the default one so the user never downloads ".pdf".
        if (name.Length == 0)
            name = Render(DefaultFormat, values);

        return name + extension;
    }

    /// <summary>
    /// Returns the placeholder keys in <paramref name="format"/> that are not supported
    /// (empty list = valid). Called when saving settings so typos are rejected early,
    /// while <see cref="Build"/> merely drops them.
    /// </summary>
    public static IReadOnlyList<string> Validate(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return [];

        return PlaceholderRegex.Matches(format)
            .Select(m => m.Groups[1].Value)
            .Where(key => !PlaceholderKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Validates several formats at once (the four per-document-type settings).
    /// Returns a human-readable error for a 400 response, or null when all formats are fine.
    /// </summary>
    public static string? ValidateFormats(params string?[] formats)
    {
        var unknown = formats.SelectMany(Validate).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            return $"Unknown file name placeholder(s): {string.Join(", ", unknown)}. " +
                   $"Allowed: {string.Join(", ", PlaceholderKeys)}.";
        }

        return formats.Any(f => f is { Length: > MaxFormatLength })
            ? $"A file name format must not be longer than {MaxFormatLength} characters."
            : null;
    }

    /// <summary>
    /// First non-blank format wins: client setting, then the issuer's company-wide default.
    /// Null means "use <see cref="DefaultFormat"/>" (Build handles that).
    /// </summary>
    public static string? ResolveFormat(string? clientFormat, string? issuerFormat) =>
        !string.IsNullOrWhiteSpace(clientFormat) ? clientFormat
        : !string.IsNullOrWhiteSpace(issuerFormat) ? issuerFormat
        : null;

    /// <summary>
    /// Localized document type word used for <c>{{DocumentType}}</c>. Server-side, so it cannot use
    /// the UI resx; the cs wording mirrors the FileName_* keys in SharedResource.resx.
    /// "cs" is the application default, so unknown languages get Czech as well.
    /// </summary>
    public static string DocumentTypeLabel(EDocumentType type, string? language)
    {
        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        return type switch
        {
            EDocumentType.CreditNote => english ? "CreditNote" : "Dobropis",
            EDocumentType.Proforma => "Proforma",
            EDocumentType.TaxReceiptForAdvance => english ? "TaxReceipt" : "Danovy_doklad",
            _ => english ? "Invoice" : "Faktura"
        };
    }

    /// <summary>Replaces placeholders, then sanitizes and shortens the result (no extension).</summary>
    private static string Render(string format, DocumentFileNameValues v)
    {
        var rendered = PlaceholderRegex.Replace(format, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "documenttype" => DocumentTypeLabel(v.DocumentType, v.Language),
            // Drafts have no number yet — the id keeps the name unique and identifiable.
            "documentnumber" => string.IsNullOrWhiteSpace(v.DocumentNumber) ? v.InvoiceId.ToString() : v.DocumentNumber,
            "variablesymbol" => v.VariableSymbol ?? "",
            "issuedate" => v.IssueDate?.ToString("yyyy-MM-dd") ?? "",
            "year" => v.IssueDate?.ToString("yyyy") ?? "",
            "month" => v.IssueDate?.ToString("MM") ?? "",
            "clientname" => v.ClientName ?? "",
            "issuername" => v.IssuerName ?? "",
            _ => "" // unknown placeholder: dropped (rejected earlier at save time)
        });

        return Sanitize(rendered);
    }

    /// <summary>
    /// Makes the text a safe file name: invalid characters become '_', the length is capped
    /// and leading/trailing spaces and dots are trimmed (Windows silently strips them, which would
    /// make the name differ from what we announced in Content-Disposition).
    /// </summary>
    private static string Sanitize(string text)
    {
        var cleaned = new string(text
            .Select(c => char.IsControl(c) || InvalidChars.Contains(c) ? '_' : c)
            .ToArray());

        cleaned = cleaned.Trim().Trim('.').Trim();
        if (cleaned.Length > MaxLength)
            cleaned = cleaned[..MaxLength].Trim().Trim('.').Trim();

        return cleaned;
    }
}
