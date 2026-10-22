namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Decides which UI culture the app starts with. Pure logic (no JS, no HTTP) so it is easy to test.
/// </summary>
public static class UiCultureResolver
{
    public const string Czech = "cs-CZ";
    public const string English = "en-US";

    /// <summary>
    /// True for the two cultures we ship resx files for.
    /// </summary>
    public static bool IsSupported(string? culture) => culture is Czech or English;

    /// <summary>
    /// Returns the logged-in user's cached culture when it is supported; otherwise falls back to
    /// the browser language: "en*" (en-GB, en-US, ...) gives English, anything else (or null) Czech.
    /// </summary>
    public static string Resolve(string? storedUserCulture, string? browserLanguage)
    {
        if (IsSupported(storedUserCulture))
            return storedUserCulture!;

        return browserLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? English : Czech;
    }
}
