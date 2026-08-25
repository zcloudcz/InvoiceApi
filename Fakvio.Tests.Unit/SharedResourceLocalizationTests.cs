using System.Globalization;
using Fakvio.UI.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests verifying that IStringLocalizer&lt;SharedResource&gt; correctly resolves
/// localization keys from .resx files in both Czech (default) and English cultures.
///
/// These tests catch common localization misconfigurations:
/// - Missing ResourcesPath in AddLocalization() → embedded resource name mismatch
/// - Missing Microsoft.Extensions.Localization NuGet → IStringLocalizer returns raw keys
/// - Marker class in wrong namespace → "double Resources" path trap
/// - Missing .resx files → all keys return ResourceNotFound = true
/// </summary>
public class SharedResourceLocalizationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IStringLocalizer<SharedResource> _localizer;

    public SharedResourceLocalizationTests()
    {
        // Build a minimal DI container with localization services,
        // exactly as the real app does in ServiceCollectionExtensions.AddSharedUiServices().
        var services = new ServiceCollection();

        // AddLogging() is required because ResourceManagerStringLocalizerFactory
        // depends on ILoggerFactory for diagnostic logging.
        services.AddLogging();

        // ResourcesPath = "Resources" is critical — without it, the factory looks for
        // "Fakvio.UI.Shared.SharedResource" but the actual embedded resource is
        // "Fakvio.UI.Shared.Resources.SharedResource" (because .resx files are in Resources/).
        services.AddLocalization(options => options.ResourcesPath = "Resources");

        _serviceProvider = services.BuildServiceProvider();
        _localizer = _serviceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    // ── Czech (default) culture tests ─────────────────────────────────────

    [Theory]
    [InlineData("Login_Title", "Přihlášení")]
    [InlineData("Btn_Login", "Přihlásit se")]
    [InlineData("Label_Email", "E-mail")]
    [InlineData("Label_Password", "Heslo")]
    [InlineData("Nav_Dashboard", "Dashboard")]
    // Fallback text shown in the chat bubble instead of a stack trace (issue #156).
    [InlineData("Chat_Error_Unexpected", "Zprávu se nepodařilo zpracovat. Zkuste to prosím znovu.")]
    public void Czech_Keys_ShouldReturn_CzechValues(string key, string expectedValue)
    {
        // Arrange — set Czech culture for this thread
        CultureInfo.CurrentUICulture = new CultureInfo("cs-CZ");

        // Act — resolve the localization key
        var result = _localizer[key];

        // Assert — the key should be found and the value should match
        result.ResourceNotFound.ShouldBeFalse(
            $"Key '{key}' was not found in Czech resources. " +
            "Check that Resources/SharedResource.resx exists and contains this key.");
        result.Value.ShouldBe(expectedValue);
    }

    // ── English culture tests ─────────────────────────────────────────────

    [Theory]
    [InlineData("Login_Title", "Login")]
    [InlineData("Btn_Login", "Log in")]
    [InlineData("Label_Email", "Email")]
    [InlineData("Label_Password", "Password")]
    [InlineData("Nav_Dashboard", "Dashboard")]
    // Fallback text shown in the chat bubble instead of a stack trace (issue #156).
    [InlineData("Chat_Error_Unexpected", "The message could not be processed. Please try again.")]
    public void English_Keys_ShouldReturn_EnglishValues(string key, string expectedValue)
    {
        // Arrange — set English culture for this thread
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");

        // Act
        var result = _localizer[key];

        // Assert
        result.ResourceNotFound.ShouldBeFalse(
            $"Key '{key}' was not found in English resources. " +
            "Check that Resources/SharedResource.en.resx exists and contains this key.");
        result.Value.ShouldBe(expectedValue);
    }

    /// <summary>
    /// Issue #152 — English wording of the two keys the set-password page shows when the
    /// workspace could not be provisioned.
    ///
    /// The Czech side is already covered by SetPasswordPageTests, which renders the page in
    /// the default culture. English has no such cover, and a key that is missing from
    /// SharedResource.en.resx silently falls back to the Czech neutral resource with
    /// ResourceNotFound = false — so only asserting the English text catches a translation
    /// that was never written.
    ///
    /// Deliberately a separate theory instead of two more rows on
    /// <see cref="English_Keys_ShouldReturn_EnglishValues"/>: that InlineData list is a
    /// standing merge-conflict anchor, because every PR that adds a key appends to it.
    /// The assertions themselves are not duplicated — this only supplies the data.
    /// </summary>
    [Theory]
    [InlineData("SetPassword_WorkspaceNotReady",
        "Your password has been set, but your workspace could not be prepared.")]
    [InlineData("SetPassword_WorkspaceNotReadyHint",
        "Logging in will not work yet. An administrator will finish the setup — please try again later or contact support.")]
    public void SetPasswordWorkspaceKeys_ShouldReturn_EnglishValues(string key, string expectedValue)
        => English_Keys_ShouldReturn_EnglishValues(key, expectedValue);

    // ── General localization health checks ────────────────────────────────

    [Fact]
    public void Localizer_ShouldNotReturn_RawKeys_ForKnownKeys()
    {
        // This test verifies the root cause of the reported bug: the localizer was
        // returning raw keys (e.g., "Layout_AppTitle") instead of translated values.
        CultureInfo.CurrentUICulture = new CultureInfo("cs-CZ");

        // These are the exact keys the user reported seeing as raw text in the UI
        var keysReportedBroken = new[] { "Layout_AppTitle", "Login_Title", "Btn_Login" };

        foreach (var key in keysReportedBroken)
        {
            var result = _localizer[key];
            // If ResourceNotFound is true, the localizer returns the key itself as value
            result.ResourceNotFound.ShouldBeFalse(
                $"Key '{key}' not found — localizer returns the raw key instead of translated text.");
            // The translated value must NOT equal the key itself
            result.Value.ShouldNotBe(key,
                $"Key '{key}' returned itself as value — localization is not working.");
        }
    }

    [Fact]
    public void CzechAndEnglish_ShouldReturn_DifferentValues_ForTranslatedKeys()
    {
        // Verify that Czech and English actually return different translations
        // (not all keys differ — e.g., "Dashboard" is the same in both languages)
        var key = "Btn_Login"; // Czech: "Přihlásit se", English: "Log in"

        CultureInfo.CurrentUICulture = new CultureInfo("cs-CZ");
        var czechValue = _localizer[key].Value;

        CultureInfo.CurrentUICulture = new CultureInfo("en-US");
        var englishValue = _localizer[key].Value;

        czechValue.ShouldNotBe(englishValue,
            "Czech and English should return different values for 'Btn_Login'.");
    }

    [Fact]
    public void EpoSettingsKeys_ShouldBeTranslated_InBothCultures()
    {
        // The EPO settings section (issue #158) is user-facing, so every one of its
        // labels must exist in both resource files — the UI is CZ/EN.
        var epoKeys = new[]
        {
            "Epo_SettingsTitle", "Epo_SettingsInfoText",
            "Epo_TaxOfficeCode", "Epo_TaxOfficeCodeHelper",
            "Epo_TaxOfficeBranchCode", "Epo_TaxOfficeBranchCodeHelper",
            "Epo_ContactPhone", "Epo_ContactEmail", "Epo_AuthorizedPersonName",
            "EpoHeaderIncompleteAskAdmin"
        };

        foreach (var culture in new[] { "cs-CZ", "en-US" })
        {
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            foreach (var key in epoKeys)
            {
                var result = _localizer[key];
                result.ResourceNotFound.ShouldBeFalse(
                    $"Key '{key}' is missing from the {culture} resources.");
            }
        }
    }

    [Fact]
    public void NonExistentKey_ShouldReturn_ResourceNotFound()
    {
        // Verify that a key that doesn't exist in .resx returns ResourceNotFound = true
        CultureInfo.CurrentUICulture = new CultureInfo("cs-CZ");

        var result = _localizer["This_Key_Does_Not_Exist_In_Any_Resx"];

        result.ResourceNotFound.ShouldBeTrue(
            "A non-existent key should have ResourceNotFound = true.");
    }
}
