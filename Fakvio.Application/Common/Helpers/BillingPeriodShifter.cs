using System.Text.RegularExpressions;

namespace Fakvio.Application.Common.Helpers;

/// <summary>
/// Moves billing periods named in free text forward (or back) by a number of months, so a copied
/// or recurring invoice saying "Hosting 3/2026" becomes "Hosting 4/2026".
///
/// Recognised periods (always with a 4-digit year 20xx unless noted):
///   "3/2026", "03/2026"  ·  "2026-03"  ·  "Q1/2026", "Q1 2026", "1Q 2026"  ·
///   Czech month names "březen"/"března"/"březnu" (year optional)  ·  English "March 2026" (year required).
/// Deliberately NOT touched: full dates ("15. března 2026", "2026-03-15", "15/3/2026"), invoice numbers
/// ("2026001"), fractions/decimals ("3/4", "3/2"), IBANs and anything else not matching the shapes above.
///
/// All shapes are matched by ONE combined regex in a single pass, so a replaced value is never
/// re-scanned (a range "03/2026-04/2026" moves each end exactly once).
/// </summary>
public static class BillingPeriodShifter
{
    // Czech nominative, genitive and locative, index 0 = January. Case of the first letter is preserved.
    private static readonly string[][] CsForms =
    [
        ["leden", "únor", "březen", "duben", "květen", "červen", "červenec", "srpen", "září", "říjen", "listopad", "prosinec"],
        ["ledna", "února", "března", "dubna", "května", "června", "července", "srpna", "září", "října", "listopadu", "prosince"],
        ["lednu", "únoru", "březnu", "dubnu", "květnu", "červnu", "červenci", "srpnu", "září", "říjnu", "listopadu", "prosinci"],
    ];
    private static readonly string[] EnNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    // Longest first so "červenec" wins over "červen".
    private static readonly string CsAlternation =
        string.Join("|", CsForms.SelectMany(f => f).Distinct().OrderByDescending(s => s.Length));

    private static readonly Regex Combined = new(
        // 3/2026, 03/2026 — not preceded by digit . / , or a letter; not followed by digit or / (so "15/3/2026", "Q1/2026" stay out).
        @"(?<![\d./,\p{L}])(?<sm>0?[1-9]|1[0-2])/(?<sy>20\d{2})(?![\d/])" +
        // 2026-03 — not part of a full date 2026-03-15.
        @"|(?<![\d-])(?<iy>20\d{2})-(?<im>0[1-9]|1[0-2])(?![\d-])" +
        // Q1/2026, Q1 2026
        @"|(?<!\p{L})Q(?<pq>[1-4])(?<ps>\s?/\s?|\s)(?<py>20\d{2})(?!\d)" +
        // 1Q 2026
        @"|(?<![\d\p{L}])(?<sq>[1-4])Q(?<ss>\s?/\s?|\s)(?<sqy>20\d{2})(?!\d)" +
        // Czech month name, optional year; skip "15. března 2026" / "15 března".
        @"|(?<!\d\.?\s?)(?<![\p{L}])(?<cn>" + CsAlternation + @")(?!\p{L})(?<cy>\s+20\d{2})?" +
        // English month name + year; skip "15 March 2026".
        @"|(?<!\d\.?\s?)(?<![\p{L}])(?<en>" + string.Join("|", EnNames) + @")(?<ey>\s+)(?<eyy>20\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns <paramref name="text"/> with every recognised period moved by <paramref name="months"/>.</summary>
    public static string? Shift(string? text, int months)
    {
        if (string.IsNullOrEmpty(text) || months == 0) return text;

        // Quarters move only by whole quarters: a 1-month shift leaves "Q1" alone.
        var quarters = months / 3;

        return Combined.Replace(text, m =>
        {
            if (m.Groups["sm"].Success)
            {
                var (year, month) = Move(int.Parse(m.Groups["sy"].Value), int.Parse(m.Groups["sm"].Value), months);
                // Keep the zero padding the author used ("03/2026" → "04/2026", "3/2026" → "4/2026").
                return $"{(m.Groups["sm"].Value.StartsWith('0') ? month.ToString("00") : month.ToString())}/{year}";
            }
            if (m.Groups["im"].Success)
            {
                var (year, month) = Move(int.Parse(m.Groups["iy"].Value), int.Parse(m.Groups["im"].Value), months);
                return $"{year}-{month:00}";
            }
            if (m.Groups["pq"].Success)
            {
                if (quarters == 0) return m.Value;
                var (year, q) = MoveQuarter(int.Parse(m.Groups["py"].Value), int.Parse(m.Groups["pq"].Value), quarters);
                return $"{m.Value[0]}{q}{m.Groups["ps"].Value}{year}";
            }
            if (m.Groups["sq"].Success)
            {
                if (quarters == 0) return m.Value;
                var (year, q) = MoveQuarter(int.Parse(m.Groups["sqy"].Value), int.Parse(m.Groups["sq"].Value), quarters);
                return $"{q}{m.Value[1]}{m.Groups["ss"].Value}{year}";
            }
            if (m.Groups["cn"].Success)
            {
                var word = m.Groups["cn"].Value;
                var lower = word.ToLowerInvariant();
                // Same grammatical form out as in (nominative / genitive / locative).
                var forms = CsForms.First(f => Array.IndexOf(f, lower) >= 0);
                var hasYear = m.Groups["cy"].Success;
                var (year, month) = Move(hasYear ? int.Parse(m.Groups["cy"].Value.Trim()) : 2000, Array.IndexOf(forms, lower) + 1, months);
                var name = MatchCase(word, forms[month - 1]);
                return hasYear ? $"{name}{m.Groups["cy"].Value[..^4]}{year}" : name;
            }
            {
                var word = m.Groups["en"].Value;
                var (year, month) = Move(int.Parse(m.Groups["eyy"].Value), Array.FindIndex(EnNames, n => n.Equals(word, StringComparison.OrdinalIgnoreCase)) + 1, months);
                return $"{MatchCase(word, EnNames[month - 1])}{m.Groups["ey"].Value}{year}";
            }
        });
    }

    /// <summary>Adds <paramref name="months"/> to a (year, 1-based month) pair, rolling the year over as needed.</summary>
    private static (int Year, int Month) Move(int year, int month, int months)
    {
        var index = year * 12 + (month - 1) + months;
        return (index / 12, index % 12 + 1);
    }

    private static (int Year, int Quarter) MoveQuarter(int year, int quarter, int quarters)
    {
        var index = year * 4 + (quarter - 1) + quarters;
        return (index / 4, index % 4 + 1);
    }

    /// <summary>Re-applies the original word's casing style (Capitalised / lower / UPPER) to <paramref name="replacement"/>.</summary>
    private static string MatchCase(string original, string replacement)
    {
        if (original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c))) return replacement.ToUpperInvariant();
        return char.IsUpper(original[0]) ? char.ToUpperInvariant(replacement[0]) + replacement[1..] : replacement;
    }
}
