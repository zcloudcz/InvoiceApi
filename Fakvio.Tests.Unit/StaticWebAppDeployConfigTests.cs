using System.Text.Json;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Guards <c>Fakvio.BlazorUI/wwwroot/staticwebapp.config.json</c> — the routing contract the
/// TEST-ENV deploy (<c>.github/workflows/blazorui-test-deploy.yml</c>) relies on.
///
/// No C# code reads this file: Azure Static Web Apps does, at runtime, in the test environment.
/// That is exactly why it needs a test. Nothing else in the repository would notice if the file
/// were deleted or its rules broken — the failure would only show up as broken deep links (or an
/// app that does not boot at all) after a deploy nobody watches.
///
/// The file lives in <c>wwwroot</c>, so the WASM publish copies it into the deployed root
/// verbatim; asserting on the repository copy therefore asserts on what actually ships.
/// </summary>
public class StaticWebAppDeployConfigTests
{
    // The SPA entry document. Every deep link (/invoices/42) must be served this file, because
    // a standalone WASM app resolves routes on the client — the host has no such path on disk.
    private const string AppShellDocument = "/index.html";

    // Asset roots that must NOT fall back to the app shell. Without these, a request for a
    // missing asset answers 200 + HTML instead of 404, and the runtime chokes on HTML where it
    // expected an assembly or a stylesheet — a far harder failure to diagnose than a plain 404.
    private const string FrameworkRoot = "/_framework/*";
    private const string RazorClassLibraryRoot = "/_content/*";
    private const string ThirdPartyLibraryRoot = "/lib/*";

    // Path from the test binary (bin/<cfg>/<tfm>) up to the repository root, then to the file
    // under test. Mirrors how DatabaseConnectivitySmokeTests reaches Fakvio.API/appsettings.json.
    private static readonly string ConfigPath = Path.Combine(
        Directory.GetCurrentDirectory(), "..", "..", "..", "..",
        "Fakvio.BlazorUI", "wwwroot", "staticwebapp.config.json");

    [Fact]
    public void NavigationFallback_ShouldRewriteDeepLinksToAppShell()
    {
        var navigationFallback = ReadNavigationFallback();

        navigationFallback.GetProperty("rewrite").GetString().ShouldBe(AppShellDocument,
            $"Deep links must be rewritten to {AppShellDocument}, otherwise refreshing any page " +
            "other than the root returns 404 on Azure Static Web Apps.");
    }

    [Theory]
    [InlineData(FrameworkRoot)]
    [InlineData(RazorClassLibraryRoot)]
    [InlineData(ThirdPartyLibraryRoot)]
    public void NavigationFallback_ShouldExcludeAssetRoot(string assetRoot)
    {
        ReadFallbackExclusions().ShouldContain(assetRoot);
    }

    // Extensions the published wwwroot actually ships. "wasm" carries the assemblies and "br"/"gz"
    // the precompressed copies a static host serves in their place — an app whose assemblies are
    // answered with the app shell does not start at all.
    [Theory]
    [InlineData("wasm")]
    [InlineData("js")]
    [InlineData("css")]
    [InlineData("json")]
    [InlineData("png")]
    [InlineData("webmanifest")]
    [InlineData("dat")]
    [InlineData("br")]
    [InlineData("gz")]
    public void NavigationFallback_ShouldExcludeAssetExtension(string extension)
    {
        // The extensions travel as one brace-glob entry ("*.{css,js,...}"), so membership is a
        // substring check on that entry rather than a lookup of a standalone list item.
        var extensionGlob = ReadFallbackExclusions()
            .Where(exclusion => exclusion.Contains(".{"))
            .ToList()
            .ShouldHaveSingleItem();

        extensionGlob.ShouldContain(extension);
    }

    private static JsonElement ReadNavigationFallback()
    {
        File.Exists(ConfigPath).ShouldBeTrue(
            $"staticwebapp.config.json is missing at {ConfigPath}. The TEST-ENV deploy publishes " +
            "wwwroot as-is, so a missing file silently ships an app with broken routing.");

        using var document = JsonDocument.Parse(File.ReadAllText(ConfigPath));

        // Clone: the JsonDocument owns the pooled buffer behind the element and is disposed here.
        return document.RootElement.GetProperty("navigationFallback").Clone();
    }

    private static IReadOnlyList<string> ReadFallbackExclusions() =>
        ReadNavigationFallback().GetProperty("exclude")
            .EnumerateArray()
            .Select(exclusion => exclusion.GetString()!)
            .ToList();
}
