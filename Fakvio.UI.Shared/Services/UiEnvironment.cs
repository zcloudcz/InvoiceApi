namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Which environment this UI build talks to, read from <c>wwwroot/appsettings.json</c>
/// (<c>Environment:Name</c>). The test deploy workflow rewrites the value to "test" before
/// publish, the same way it rewrites the API URL.
/// </summary>
/// <remarks>
/// WHY: test.fakvio.cz and app.fakvio.cz are pixel-identical otherwise, and a SysAdmin with
/// both open in tabs will sooner or later edit production thinking it is the test system.
/// A pink app bar and a TEST chip make the two impossible to confuse.
/// Pure functions so the rule is unit-testable without rendering the layout.
/// </remarks>
public static class UiEnvironment
{
    public const string ConfigKey = "Environment:Name";

    /// <summary>Anything that is not blank and not "production" is a non-production environment.</summary>
    public static bool IsNonProduction(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !name.Trim().Equals("production", StringComparison.OrdinalIgnoreCase);

    /// <summary>Inline style for the app bar: pink on non-production, nothing (theme default) otherwise.</summary>
    public static string? AppBarStyle(string? name) =>
        IsNonProduction(name) ? "background-color:#e91e63" : null;
}
