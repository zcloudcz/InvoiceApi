namespace Fakvio.Infrastructure.Service.Oss;

/// <summary>
/// The 27 EU member states (ISO 3166-1 alpha-2), used by the OSS detector to tell
/// "another EU state" (→ OSS candidate) apart from "non-EU" (→ ordinary export, out of
/// scope of OSS) and from "CZ" (→ ordinary domestic DPH).
///
/// Kept as its own tiny lookup rather than reusing any single other list in the codebase
/// (none is a perfect fit — the UBL/Peppol work only has a handful of country NAME→ISO
/// mappings, not a membership list) — pure data, no I/O, trivially testable.
/// </summary>
public static class EuCountries
{
    /// <summary>All 27 EU member states, CZ included.</summary>
    public static readonly IReadOnlySet<string> MemberStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE"
    };

    /// <summary>Is this ISO2 code an EU member state (CZ included)?</summary>
    public static bool IsMemberState(string? countryCode) =>
        countryCode != null && MemberStates.Contains(countryCode);

    /// <summary>Is this ISO2 code an EU member state OTHER than Czechia — an OSS candidate country?</summary>
    public static bool IsOtherMemberState(string? countryCode) =>
        IsMemberState(countryCode) && !string.Equals(countryCode, "CZ", StringComparison.OrdinalIgnoreCase);

    // Free-text address country names (Czech + English + native) of the EU states. Address.Country is a plain
    // text field, so "Francie", "France" and "FR" must all resolve; UblCodes only knows a handful of names.
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Rakousko"] = "AT", ["Austria"] = "AT", ["Österreich"] = "AT",
        ["Belgie"] = "BE", ["Belgium"] = "BE", ["België"] = "BE", ["Belgique"] = "BE",
        ["Bulharsko"] = "BG", ["Bulgaria"] = "BG",
        ["Chorvatsko"] = "HR", ["Croatia"] = "HR", ["Hrvatska"] = "HR",
        ["Kypr"] = "CY", ["Cyprus"] = "CY",
        ["Česko"] = "CZ", ["Česká republika"] = "CZ", ["Czechia"] = "CZ", ["Czech Republic"] = "CZ",
        ["Dánsko"] = "DK", ["Denmark"] = "DK", ["Danmark"] = "DK",
        ["Estonsko"] = "EE", ["Estonia"] = "EE", ["Eesti"] = "EE",
        ["Finsko"] = "FI", ["Finland"] = "FI", ["Suomi"] = "FI",
        ["Francie"] = "FR", ["France"] = "FR",
        ["Německo"] = "DE", ["Germany"] = "DE", ["Deutschland"] = "DE",
        ["Řecko"] = "GR", ["Greece"] = "GR", ["Ελλάδα"] = "GR", ["EL"] = "GR",
        ["Maďarsko"] = "HU", ["Hungary"] = "HU", ["Magyarország"] = "HU",
        ["Irsko"] = "IE", ["Ireland"] = "IE",
        ["Itálie"] = "IT", ["Italy"] = "IT", ["Italia"] = "IT",
        ["Lotyšsko"] = "LV", ["Latvia"] = "LV", ["Latvija"] = "LV",
        ["Litva"] = "LT", ["Lithuania"] = "LT", ["Lietuva"] = "LT",
        ["Lucembursko"] = "LU", ["Luxembourg"] = "LU", ["Luxemburg"] = "LU",
        ["Malta"] = "MT",
        ["Nizozemsko"] = "NL", ["Netherlands"] = "NL", ["Nederland"] = "NL", ["Holland"] = "NL",
        ["Polsko"] = "PL", ["Poland"] = "PL", ["Polska"] = "PL",
        ["Portugalsko"] = "PT", ["Portugal"] = "PT",
        ["Rumunsko"] = "RO", ["Romania"] = "RO", ["România"] = "RO",
        ["Slovensko"] = "SK", ["Slovakia"] = "SK", ["Slovenská republika"] = "SK",
        ["Slovinsko"] = "SI", ["Slovenia"] = "SI", ["Slovenija"] = "SI",
        ["Španělsko"] = "ES", ["Spain"] = "ES", ["España"] = "ES",
        ["Švédsko"] = "SE", ["Sweden"] = "SE", ["Sverige"] = "SE"
    };

    /// <summary>
    /// Normalizes a free-text address country (ISO2 code or a Czech/English/native name) to an ISO2 code;
    /// empty = "CZ" (the app-wide default), unknown text = null (never guessed — then the invoice is not OSS).
    /// </summary>
    public static string? ToIso2(string? country)
    {
        if (string.IsNullOrWhiteSpace(country)) return "CZ";
        var trimmed = country.Trim();
        if (trimmed.Length == 2)
            return Names.TryGetValue(trimmed, out var alias) ? alias : trimmed.ToUpperInvariant(); // "EL" is the VAT-id prefix of Greece
        return Names.TryGetValue(trimmed, out var code) ? code : null;
    }
}
