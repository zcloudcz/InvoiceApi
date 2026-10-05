using System.Globalization;
using System.Text;

namespace Fakvio.Application.Service;

/// <summary>One 074 statement header plus the 075 items that follow it.</summary>
public class GpcStatement
{
    /// <summary>Own account: 6-digit prefix + 10-digit number, zero-padded (16 chars).</summary>
    public string Account { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public DateTime StatementDate { get; set; }
    public int SequenceNumber { get; set; }
    public List<GpcItem> Items { get; set; } = new();
}

/// <summary>One 075 item (a single payment on the statement).</summary>
public class GpcItem
{
    public int LineNumber { get; set; }
    public string RawLine { get; set; } = string.Empty;

    /// <summary>Counterparty account as "prefix-number/bank" (prefix/bank omitted when zero); null when absent.</summary>
    public string? CounterAccount { get; set; }
    public string? DocumentNumber { get; set; }

    /// <summary>Always positive; <see cref="PostingCode"/> tells the direction.</summary>
    public decimal Amount { get; set; }

    /// <summary>1 = debit, 2 = credit, 4 = debit storno, 5 = credit storno.</summary>
    public int PostingCode { get; set; }

    public string? VariableSymbol { get; set; }
    public string? ConstantSymbol { get; set; }
    public string? SpecificSymbol { get; set; }
    public DateTime ValueDate { get; set; }
    public string? Name { get; set; }
}

public class GpcParseResult
{
    public List<GpcStatement> Statements { get; } = new();
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Pure parser of the Czech GPC / ABO bank statement format (fixed-width 128-char lines,
/// Windows-1250 or UTF-8). Understands 074 (statement header) and 075 (item) records;
/// 076/078/079 additional-info records and unknown lines are ignored. Malformed lines are
/// skipped and reported in <see cref="GpcParseResult.Errors"/> instead of failing the whole file.
///
/// 075 layout (1-based): 4-19 own account, 20-35 counter account (prefix 6 + number 10),
/// 36-48 document number, 49-60 amount in halere, 61 posting code, 62-71 VS,
/// 72-75 counter bank code, 76-81 KS, 82-91 SS, 92-97 value date DDMMYY, 98-117 name,
/// 123-128 posting date (fallback when the value date is blank).
/// </summary>
public static class GpcParser
{
    private const int LineWidth = 128;

    /// <summary>Decodes bytes (strict UTF-8, otherwise Windows-1250) and parses them.</summary>
    public static GpcParseResult Parse(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = Encoding.GetEncoding(1250).GetString(content);
        }
        return Parse(text.TrimStart('﻿'));
    }

    public static GpcParseResult Parse(string text)
    {
        var result = new GpcParseResult();
        GpcStatement? current = null;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length < 3) continue;
            var no = i + 1;
            try
            {
                if (line.StartsWith("074"))
                {
                    line = line.PadRight(LineWidth);
                    current = new GpcStatement
                    {
                        Account = line.Substring(3, 16),
                        AccountName = line.Substring(19, 20).Trim(),
                        SequenceNumber = int.TryParse(line.AsSpan(105, 3), out var seq) ? seq : 0,
                        StatementDate = ParseDate(line.Substring(108, 6)),
                    };
                    if (!IsDigits(current.Account)) throw new FormatException("invalid account number");
                    result.Statements.Add(current);
                }
                else if (line.StartsWith("075"))
                {
                    if (current == null) throw new FormatException("item before any 074 header");
                    line = line.PadRight(LineWidth);
                    if (!IsDigits(line.Substring(48, 12))) throw new FormatException("invalid amount");
                    var code = line[60] - '0';
                    if (code is not (1 or 2 or 4 or 5)) throw new FormatException("invalid posting code");

                    current.Items.Add(new GpcItem
                    {
                        LineNumber = no,
                        RawLine = line.TrimEnd(),
                        CounterAccount = FormatCounterAccount(line.Substring(19, 16), line.Substring(71, 4)),
                        DocumentNumber = NullIfBlank(line.Substring(35, 13)),
                        Amount = decimal.Parse(line.Substring(48, 12), CultureInfo.InvariantCulture) / 100m,
                        PostingCode = code,
                        VariableSymbol = Symbol(line.Substring(61, 10)),
                        ConstantSymbol = Symbol(line.Substring(75, 6)),
                        SpecificSymbol = Symbol(line.Substring(81, 10)),
                        ValueDate = TryParseDate(line.Substring(91, 6)) ?? TryParseDate(line.Substring(122, 6)) ?? current.StatementDate,
                        Name = NullIfBlank(line.Substring(97, 20)),
                    });
                }
                // 076/078/079 and anything else: ignored on purpose.
            }
            catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
            {
                result.Errors.Add($"Line {no}: {ex.Message}");
            }
        }
        return result;
    }

    private static bool IsDigits(string s) => s.All(char.IsAsciiDigit);

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Digits-only symbol without leading zeros; null when empty/zero/non-numeric.</summary>
    private static string? Symbol(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || !IsDigits(s)) return null;
        s = s.TrimStart('0');
        return s.Length == 0 ? null : s;
    }

    private static string? FormatCounterAccount(string account16, string bankCode)
    {
        if (!IsDigits(account16)) return null;
        var number = account16[6..].TrimStart('0');
        if (number.Length == 0) return null; // cash operation, fee, ...
        var prefix = account16[..6].TrimStart('0');
        var result = prefix.Length > 0 ? $"{prefix}-{number}" : number;
        return IsDigits(bankCode) && bankCode != "0000" ? $"{result}/{bankCode}" : result;
    }

    private static DateTime ParseDate(string ddmmyy) =>
        TryParseDate(ddmmyy) ?? throw new FormatException($"invalid date '{ddmmyy}'");

    private static DateTime? TryParseDate(string ddmmyy) =>
        DateTime.TryParseExact(ddmmyy, "ddMMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : null;
}
