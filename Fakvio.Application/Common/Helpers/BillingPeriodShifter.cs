using System.Text.RegularExpressions;

namespace Fakvio.Application.Common.Helpers;

/// <summary>
/// Moves billing periods named in free text forward (or back) by a number of months, so a copied
/// or recurring invoice saying "Hosting 3/2026" becomes "Hosting 4/2026".
///
/// Recognised periods (always with a 4-digit year 20xx unless noted):
///   "3/2026", "03/2026"  ·  "2026-03"  ·  "Q1/2026", "Q1 2026", "1Q 2026"  ·
///   Czech month names "březen"/"března" (year optional)  ·  English "March 2026" (year required).
/// Deliberately NOT touched: full dates ("15. března 2026", "2026-03-15", "15/3/2026"), invoice numbers
/// ("2026001"), fractions/decimals ("3/4", "3/2"), IBANs and anything else not matching the shapes above.
/// </summary>
public static class BillingPeriodShifter
{
    // Czech nominative + genitive, index 0 = January. Case of the first letter is preserved.
    private static readonly string[] CsNom =
        ["leden", "únor", "březen", "duben", "květen", "červen", "červenec", "srpen", "září", "říjen", "listopad", "prosinec"];
    private static readonly string[] CsGen =
        ["ledna", "února", "března", "dubna", "května", "června", "července", "srpna", "září", "října", "listopadu", "prosince"];
    private static readonly string[] EnNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    // Not preceded by a digit/dot/slash/letter, not followed by a digit or slash (so "15/3/2026" and "Q1/2026" stay out).
    private static readonly Regex SlashMonth = new(@"(?<![\d./,\p{L}])(0?[1-9]|1[0-2])/(20\d{2})(?![\d/])", RegexOptions.Compiled);
    private static readonly Regex IsoMonth = new(@"(?<![\d-])(20\d{2})-(0[1-9]|1[0-2])(?![\d-])", RegexOptions.Compiled);
    private static readonly Regex QuarterPrefix = new(@"(?<!\p{L})Q([1-4])(\s?/\s?|\s)(20\d{2})(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuarterSuffix = new(@"(?<![\d\p{L}])([1-4])Q(\s?/\s?|\s)(20\d{2})(?!\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Month name optionally followed by a year. The lookbehind skips dates like "15. března 2026" / "15 March 2026".
    private static readonly Regex CsMonthName = new(
        $@"(?<!\d\.?\s?)(?<![\p{{L}}])({string.Join("|", CsNom.Concat(CsGen).Distinct().OrderByDescending(s => s.Length))})(?!\p{{L}})(\s+(20\d{{2}}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EnMonthName = new(
        $@"(?<!\d\.?\s?)(?<![\p{{L}}])({string.Join("|", EnNames)})\s+(20\d{{2}})(?!\d)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Returns <paramref name="text"/> with every recognised period moved by <paramref name="months"/>.</summary>
    public static string? Shift(string? text, int months)
    {
        if (string.IsNullOrEmpty(text) || months == 0) return text;

        text = SlashMonth.Replace(text, m =>
        {
            var (year, month) = Move(int.Parse(m.Groups[2].Value), int.Parse(m.Groups[1].Value), months);
            // Keep the zero padding the author used ("03/2026" → "04/2026", "3/2026" → "4/2026").
            return $"{(m.Groups[1].Value.StartsWith('0') ? month.ToString("00") : month.ToString())}/{year}";
        });

        text = IsoMonth.Replace(text, m =>
        {
            var (year, month) = Move(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), months);
            return $"{year}-{month:00}";
        });

        // Quarters move only by whole quarters: a 1-month shift leaves "Q1" alone.
        var quarters = months / 3;
        if (quarters != 0)
        {
            text = QuarterPrefix.Replace(text, m =>
            {
                var (year, q) = MoveQuarter(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[1].Value), quarters);
                return $"{m.Value[0]}{q}{m.Groups[2].Value}{year}";
            });
            text = QuarterSuffix.Replace(text, m =>
            {
                var (year, q) = MoveQuarter(int.Parse(m.Groups[3].Value), int.Parse(m.Groups[1].Value), quarters);
                return $"{q}{m.Value[1]}{m.Groups[2].Value}{year}";
            });
        }

        text = CsMonthName.Replace(text, m =>
        {
            var word = m.Groups[1].Value;
            var lower = word.ToLowerInvariant();
            // "září" is the same in both cases, so prefer nominative there (index lookup is identical anyway).
            var isGenitive = Array.IndexOf(CsNom, lower) < 0;
            var idx = isGenitive ? Array.IndexOf(CsGen, lower) : Array.IndexOf(CsNom, lower);
            var hasYear = m.Groups[3].Success;
            var (year, month) = Move(hasYear ? int.Parse(m.Groups[3].Value) : 2000, idx + 1, months);
            var name = (isGenitive ? CsGen : CsNom)[month - 1];
            name = MatchCase(word, name);
            return hasYear ? $"{name}{m.Groups[2].Value[..^4]}{year}" : name;
        });

        text = EnMonthName.Replace(text, m =>
        {
            var word = m.Groups[1].Value;
            var (year, month) = Move(int.Parse(m.Groups[2].Value), Array.FindIndex(EnNames, n => n.Equals(word, StringComparison.OrdinalIgnoreCase)) + 1, months);
            var gap = m.Value.Substring(word.Length, m.Value.Length - word.Length - 4);
            return $"{MatchCase(word, EnNames[month - 1])}{gap}{year}";
        });

        return text;
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
        var lower = char.ToLowerInvariant(replacement[0]) + replacement[1..];
        return char.IsUpper(original[0]) ? char.ToUpperInvariant(lower[0]) + lower[1..] : lower;
    }
}
