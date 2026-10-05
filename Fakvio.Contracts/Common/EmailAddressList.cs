using System.Net.Mail;

namespace Fakvio.Contracts.Common;

/// <summary>
/// Parsing and validation of e-mail address lists (the CC field of "send invoice by e-mail").
/// Lives in Contracts because the same rules run in the browser (inline validation of the CC text box)
/// and on the server (the trust boundary that actually decides) - one implementation, no drift.
/// </summary>
public static class EmailAddressList
{
    /// <summary>Most CC recipients one invoice e-mail may have (keeps the message from being used as a mass mailer).</summary>
    public const int MaxCc = 10;

    /// <summary>Longest valid address (RFC 5321 path limit).</summary>
    private const int MaxAddressLength = 254;

    private static readonly char[] Separators = [',', ';', '\n', '\r', '\t', ' '];

    /// <summary>
    /// Splits free text ("a@x.cz, b@y.cz; c@z.cz") into trimmed, non-empty tokens.
    /// Does NOT validate - call <see cref="ValidateCc"/> on the result.
    /// </summary>
    public static List<string> Split(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// True for a bare address like <c>jan@firma.cz</c>. A display-name form such as
    /// <c>Jan &lt;jan@firma.cz&gt;</c> is rejected on purpose: only the plain address is ever stored or sent.
    /// </summary>
    public static bool IsValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > MaxAddressLength)
            return false;

        // MailAddress.TryCreate accepts "Name <a@b.cz>" and trims; requiring the parsed Address to equal the
        // input rejects those forms (and anything with CR/LF that could inject mail headers).
        return MailAddress.TryCreate(address, out var parsed)
               && string.Equals(parsed.Address, address, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns an error message for an invalid CC list (too many entries or a malformed address),
    /// or null when the list is fine (null / empty is fine - CC is optional).
    /// </summary>
    public static string? ValidateCc(IReadOnlyCollection<string>? cc)
    {
        if (cc is null || cc.Count == 0)
            return null;

        if (cc.Count > MaxCc)
            return $"At most {MaxCc} CC recipients are allowed (got {cc.Count}).";

        var invalid = cc.FirstOrDefault(a => !IsValidAddress(a));
        return invalid is null ? null : $"CC address '{Truncate(invalid)}' is not a valid e-mail address.";
    }

    /// <summary>Trims and removes duplicates (case-insensitive) and blank entries, keeping the first spelling.</summary>
    public static List<string> Normalize(IEnumerable<string>? addresses) =>
        (addresses ?? [])
            .Select(a => a?.Trim() ?? string.Empty)
            .Where(a => a.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Echoing an unbounded user string into an error message / log is a log-flooding vector.
    private static string Truncate(string value) => value.Length <= 60 ? value : value[..60] + "...";
}
