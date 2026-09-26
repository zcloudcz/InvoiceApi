using System.Globalization;
using System.Text;

namespace Fakvio.Infrastructure.Import;

/// <summary>
/// Thrown when a CSV file cannot be parsed (bad encoding, unreadable structure,
/// or a file that exceeds the configured safety limits).
/// The <see cref="Message"/> is safe to show directly to the user.
/// </summary>
public sealed class CsvParseException : Exception
{
    public CsvParseException(string message) : base(message)
    {
    }
}

/// <summary>
/// A very small, tolerant CSV reader for import features (client/invoice CSV import).
///
/// We don't take a dependency on a CSV library (e.g. CsvHelper) because the format we need to
/// support is narrow: RFC 4180 quoting, a handful of delimiters, and two encodings. See
/// DEVGUIDE.md §4.14 for the rationale.
///
/// Design notes for future readers:
/// - Real exports from Fakturoid/iDoklad were not available when this was written, so the
///   parser is deliberately generic (delimiter/encoding auto-detection, unknown columns ignored)
///   rather than hard-coded to one vendor's format. See Fakvio.Tests.Unit/CsvTableTests.cs for the
///   synthetic fixtures used to validate it.
/// - Header names are normalized (trimmed, lower-cased, diacritics stripped) so that callers can
///   match them against alias tables without worrying about "Ulice" vs "ulice " vs "ULICE".
/// </summary>
public sealed class CsvTable
{
    /// <summary>Safety limit: files larger than this are rejected before parsing (avoid loading huge uploads into memory).</summary>
    public const int MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB

    /// <summary>Safety limit: files with more data rows than this are rejected (avoid pathological imports).</summary>
    public const int MaxRowCount = 10_000;

    /// <summary>
    /// Safety limit: a single record can't have more fields than this. Without this, a file that
    /// stays under <see cref="MaxFileSizeBytes"/> but is mostly delimiters (e.g. one 5 MB line of
    /// ";;;;;...") would still blow up memory — millions of empty-string fields and headers.
    /// </summary>
    public const int MaxFieldCount = 200;

    private static readonly char[] CandidateDelimiters = [';', ',', '\t'];

    /// <summary>Header names in the order they appeared in the file, normalized (see class remarks).</summary>
    public IReadOnlyList<string> Headers { get; }

    /// <summary>
    /// One dictionary per data row (header row excluded), keyed by the normalized header name.
    /// A row is only present in a dictionary if the header existed for that column index.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

    private CsvTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    /// <summary>
    /// Reads the whole stream, detects encoding and delimiter, and parses it as a CSV table.
    /// Throws <see cref="CsvParseException"/> for anything the caller should show to the user
    /// (empty file, file too large, too many rows).
    /// </summary>
    public static CsvTable Parse(Stream stream)
    {
        var bytes = ReadAllBytesWithSizeLimit(stream);
        if (bytes.Length == 0)
        {
            throw new CsvParseException("The file is empty.");
        }

        var text = DecodeText(bytes);
        var delimiter = DetectDelimiter(FirstLine(text));
        var records = Tokenize(text, delimiter);
        // Drop fully-blank records (e.g. a stray blank line, or a line with only delimiters).
        records = records.Where(r => !r.All(string.IsNullOrWhiteSpace)).ToList();

        if (records.Count == 0)
        {
            throw new CsvParseException("The file has no rows.");
        }

        var headers = records[0].Select(NormalizeHeader).ToList();
        var dataRecords = records.Skip(1).ToList();
        if (dataRecords.Count > MaxRowCount)
        {
            throw new CsvParseException($"The file has {dataRecords.Count} data rows, which exceeds the limit of {MaxRowCount}.");
        }

        var rows = new List<IReadOnlyDictionary<string, string>>(dataRecords.Count);
        foreach (var fields in dataRecords)
        {
            var row = new Dictionary<string, string>();
            for (var i = 0; i < headers.Count && i < fields.Count; i++)
            {
                row[headers[i]] = fields[i];
            }

            rows.Add(row);
        }

        return new CsvTable(headers, rows);
    }

    private static byte[] ReadAllBytesWithSizeLimit(Stream stream)
    {
        using var ms = new MemoryStream();
        // Copy in chunks so we can bail out as soon as we exceed the limit, instead of buffering
        // an arbitrarily large upload into memory first.
        var buffer = new byte[81_920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxFileSizeBytes)
            {
                throw new CsvParseException($"The file exceeds the maximum size of {MaxFileSizeBytes / (1024 * 1024)} MB.");
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Decodes raw bytes to text: a UTF-8 BOM wins outright, otherwise we try strict UTF-8 and
    /// fall back to Windows-1250 (the common Czech/Central-European legacy encoding used by
    /// older accounting tools) if that fails.
    /// </summary>
    private static string DecodeText(byte[] bytes)
    {
        var utf8Bom = Encoding.UTF8.GetPreamble();
        if (bytes.Length >= utf8Bom.Length && bytes.AsSpan(0, utf8Bom.Length).SequenceEqual(utf8Bom))
        {
            return Encoding.UTF8.GetString(bytes, utf8Bom.Length, bytes.Length - utf8Bom.Length);
        }

        try
        {
            // Throw on invalid byte sequences instead of silently replacing them with '?' —
            // that's how we notice this isn't really UTF-8 and should try Windows-1250 instead.
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var windows1250 = Encoding.GetEncoding(1250);
            return windows1250.GetString(bytes);
        }
    }

    /// <summary>
    /// Single-pass RFC 4180 tokenizer: splits the whole text into records (rows), each a list of
    /// fields. Handles quoted fields that span multiple lines or contain the delimiter, and
    /// unescapes doubled quotes ("" → ") exactly once.
    ///
    /// This used to be two passes (split into lines, then split each line into fields), but that
    /// meant the escaped-quote unescaping ran twice and corrupted values like <c>""hello""</c> —
    /// keep this as one pass.
    /// </summary>
    private static List<List<string>> Tokenize(string text, char delimiter)
    {
        var records = new List<List<string>>();
        var currentRecord = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var recordHasContent = false;

        void EndField()
        {
            if (currentRecord.Count >= MaxFieldCount)
            {
                throw new CsvParseException($"A row has more than {MaxFieldCount} columns — check the delimiter is correct.");
            }

            currentRecord.Add(current.ToString());
            current.Clear();
        }

        void EndRecord()
        {
            EndField();
            records.Add(currentRecord);
            currentRecord = new List<string>();
            recordHasContent = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    recordHasContent = true;
                    break;
                case var _ when c == delimiter:
                    EndField();
                    recordHasContent = true;
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    if (recordHasContent || current.Length > 0)
                    {
                        EndRecord();
                    }

                    break;
                case '\n':
                    if (recordHasContent || current.Length > 0)
                    {
                        EndRecord();
                    }

                    break;
                default:
                    current.Append(c);
                    recordHasContent = true;
                    break;
            }
        }

        if (inQuotes)
        {
            // Reaching EOF still inside a quote means an unterminated quoted field — without this
            // check, everything after the stray opening quote (including further rows) silently
            // gets absorbed into one field instead of erroring.
            throw new CsvParseException("The file has an unterminated quoted field (a stray \" with no matching closing quote).");
        }

        if (recordHasContent || current.Length > 0)
        {
            EndRecord();
        }

        return records;
    }

    /// <summary>Returns just the first physical line of the text — enough to sniff the delimiter from.</summary>
    private static string FirstLine(string text)
    {
        var newlineIndex = text.IndexOfAny(['\r', '\n']);
        return newlineIndex >= 0 ? text[..newlineIndex] : text;
    }

    /// <summary>
    /// Picks the delimiter that appears most often (outside quotes) in the header line, among
    /// the delimiters we support. Defaults to ';' (the most common one in Czech CSV exports) if
    /// none of the candidates appear at all.
    /// </summary>
    private static char DetectDelimiter(string headerLine)
    {
        // Count occurrences of each candidate ignoring characters inside quotes, so a quoted
        // header like "Company, s.r.o." doesn't fool the comma-detection.
        var counts = CandidateDelimiters.ToDictionary(d => d, _ => 0);
        var inQuotes = false;

        foreach (var c in headerLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && counts.ContainsKey(c))
            {
                counts[c]++;
            }
        }

        var best = counts.OrderByDescending(kv => kv.Value).First();
        return best.Value > 0 ? best.Key : ';';
    }

    /// <summary>
    /// Normalizes a header name for matching against alias tables: trim, lower-case, strip
    /// diacritics (so "Číslo účtu" and "cislo uctu" match the same alias).
    /// </summary>
    internal static string NormalizeHeader(string header)
    {
        var trimmed = header.Trim().ToLowerInvariant();
        var decomposed = trimmed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
