using System.Globalization;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Date parsing shared by the reporting chat tools.
///
/// Why it is not just <c>DateTime.TryParse</c>: the model is told to send ISO dates, but
/// Czech users dictate "15.3.2026" and the model happily forwards that. Accepting a fixed
/// list of formats keeps the behaviour identical in every tool, and — more importantly —
/// culture independent: <c>TryParse</c> would read "03/04/2026" differently on a Czech
/// workstation than on the (invariant-culture) server.
///
/// Junior note: the returned value is always <see cref="DateTimeKind.Utc"/>. Npgsql rejects
/// <c>Unspecified</c> kinds when comparing against PostgreSQL <c>timestamp with time zone</c>
/// columns, which is exactly what every date column in this database is.
/// </summary>
internal static class ChatToolDates
{
    /// <summary>Accepted input formats, in the order the model is most likely to produce them.</summary>
    private static readonly string[] AcceptedFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "dd/MM/yyyy"];

    /// <summary>
    /// Parses one date parameter. Returns false when the value is missing, blank, or not one
    /// of <see cref="AcceptedFormats"/> — the caller decides whether that is an error (required
    /// parameter) or simply "no filter" (optional parameter).
    /// </summary>
    public static bool TryParse(Dictionary<string, string> parameters, string key, out DateTime value)
    {
        value = default;

        if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        if (!DateTime.TryParseExact(raw.Trim(), AcceptedFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
            return false;

        value = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        return true;
    }

    /// <summary>
    /// Same as <see cref="TryParse"/> but for optional filters — returns null instead of false.
    /// </summary>
    public static DateTime? ParseOptional(Dictionary<string, string> parameters, string key)
        => TryParse(parameters, key, out var value) ? value : null;

    /// <summary>Formats a date for the text block handed back to the model.</summary>
    public static string Format(DateTime? date)
        => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "(none)";
}
