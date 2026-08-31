using System.Text.Json;
using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Deployment;

/// <summary>
/// Smoke tests for a *deployed* environment (test.fakvio.cz and, later, production).
///
/// WHY a separate fixture: every other test here exercises application behaviour that a local
/// `dotnet run` reproduces. These five cannot be reproduced locally at all — they check the wiring
/// that only exists once the bundle is published to Azure Static Web Apps and talks to a Function
/// App: which API URL got baked into the bundle, whether the static host answers deep links,
/// whether the browser's CORS preflight survives, whether the database is reachable through the
/// Tailscale tunnel, and whether the environment is visually distinguishable from production.
/// Each of these has already broken once (see the deployment history in ADMINGUIDE §14).
///
/// Skipped unless FAKVIO_UI_URL points at an https host, so `dotnet test` on a developer machine
/// (default http://localhost:5145) stays green and fast.
/// </summary>
[TestFixture]
[Category("Deployment")]
public class DeployedEnvironmentTests : FakvioPageTest
{
    [SetUp]
    public void SkipUnlessDeployed()
    {
        if (!Config.UiUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Ignore(
                $"Deployment smoke tests need a deployed target. Set FAKVIO_UI_URL/FAKVIO_API_URL, " +
                $"e.g. FAKVIO_UI_URL=https://test.fakvio.cz (current: {Config.UiUrl}).");
        }
    }

    /// <summary>
    /// The published bundle must call the API of *its own* environment. The deploy workflow rewrites
    /// ApiSettings:BaseUrl before publish; if that rewrite ever silently no-ops, the test frontend
    /// starts driving production data and nothing else would notice.
    /// </summary>
    [Test]
    public async Task PublishedBundle_TalksToTheApiOfItsOwnEnvironment()
    {
        var response = await Page.APIRequest.GetAsync($"{Config.UiUrl}/appsettings.json");
        Assert.That(response.Status, Is.EqualTo(200), "appsettings.json must be served next to the bundle");

        // The file carries a commented-out localhost line, which is not valid JSON — read the value
        // the same way a human would rather than parsing the document.
        var body = await response.TextAsync();
        var baseUrl = ExtractJsonStringValue(body, "BaseUrl");

        Assert.That(baseUrl?.TrimEnd('/'), Is.EqualTo(Config.ApiUrl.TrimEnd('/')),
            $"Bundle at {Config.UiUrl} points at '{baseUrl}' but the tests target '{Config.ApiUrl}'. " +
            "A test frontend wired to the production API is the failure this test exists for.");
    }

    /// <summary>
    /// Static hosts serve files, not routes: without a navigation fallback, refreshing on /invoices
    /// returns the host's 404 page instead of the Blazor shell. staticwebapp.config.json configures
    /// that fallback, and it only takes effect once deployed.
    /// </summary>
    [Test]
    public async Task DeepLink_ReturnsTheApplicationShell_NotAHostNotFound()
    {
        var response = await Page.GotoAsync("/invoices", new()
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = Config.BlazorLoadTimeout
        });

        Assert.That(response!.Status, Is.EqualTo(200), "deep link must not hit the static host's 404");

        // The shell always boots the WASM runtime; an unauthenticated visitor is then redirected to
        // /login, which is fine — the point is that the host served the app, not an error page.
        await Page.WaitForSelectorAsync("#app, .mud-layout", new() { Timeout = Config.BlazorLoadTimeout });
        await Expect(Page.GetByText("Fakvio").First).ToBeVisibleAsync(new() { Timeout = Config.BlazorLoadTimeout });
    }

    /// <summary>
    /// The browser's CORS preflight is answered by the Functions host itself, not by the app's own
    /// CORS middleware — so app settings alone are not enough and the platform CORS list has to
    /// name this origin. When it does not, the UI shows "invalid email or password" while curl
    /// logs in happily, which is exactly how this was missed once already.
    /// </summary>
    [Test]
    public async Task BrowserLogin_IsNotBlockedByCors()
    {
        var corsErrors = new List<string>();
        Page.Console += (_, message) =>
        {
            if (message.Type == "error" && message.Text.Contains("CORS", StringComparison.OrdinalIgnoreCase))
            {
                corsErrors.Add(message.Text);
            }
        };

        await LoginViaUiAsync();
        await Page.WaitForURLAsync(url => !url.Contains("/login"), new() { Timeout = Config.BlazorLoadTimeout });

        Assert.That(corsErrors, Is.Empty,
            $"Browser blocked the login call: {string.Join(" | ", corsErrors)}");
    }

    /// <summary>
    /// Proves the API can actually reach its database from the deployed host — on the test
    /// environment that means through the Tailscale tunnel. The health endpoint is SysAdmin-only,
    /// so this also confirms a real token issued against a real user table.
    /// </summary>
    [Test]
    public async Task Api_ReachesItsDatabase_FromTheDeployedHost()
    {
        var token = await AuthHelper.GetTokenAsync(Config.ApiUrl, Config.AdminEmail, Config.AdminPassword);

        var response = await Page.APIRequest.GetAsync($"{Config.ApiUrl}/api/diagnostic/health", new()
        {
            Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
        });

        var payload = await response.TextAsync();
        Assert.That(response.Status, Is.EqualTo(200),
            $"health returned {response.Status}: {payload}");

        using var health = JsonDocument.Parse(payload);
        Assert.Multiple(() =>
        {
            Assert.That(health.RootElement.GetProperty("databaseConnected").GetBoolean(), Is.True,
                "database not reachable from the deployed host");
            Assert.That(health.RootElement.GetProperty("databaseReady").GetBoolean(), Is.True,
                "database reachable but migrations are pending");
        });
    }

    /// <summary>
    /// A non-production environment must be impossible to mistake for production in an adjacent
    /// browser tab: pink app bar plus the environment name spelled out.
    /// </summary>
    [Test]
    public async Task NonProductionEnvironment_IsVisuallyMarked()
    {
        await LoginAndNavigateAsync("/", "h4, .mud-table, .mud-card");

        var appBarColour = await Page.EvaluateAsync<string?>(
            "() => { const bar = document.querySelector('.mud-appbar'); return bar ? getComputedStyle(bar).backgroundColor : null; }");

        Assert.That(appBarColour, Is.EqualTo("rgb(233, 30, 99)"),
            "the test environment's app bar must be pink (see UiEnvironment.AppBarStyle)");

        await Expect(Page.Locator(".mud-chip", new() { HasTextString = "TEST" }).First).ToBeVisibleAsync();
    }

    /// <summary>
    /// Reads "key": "value" out of a JSON-with-comments file without a strict parser.
    /// Commented-out lines are dropped first: appsettings.json keeps a `//"BaseUrl": "http://localhost:7071"`
    /// line for local development, and it sits *above* the active one — a naive match reads the
    /// comment and reports a false failure (it did, the first time this test ran).
    /// </summary>
    private static string? ExtractJsonStringValue(string json, string key)
    {
        var active = string.Join('\n', json
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var match = System.Text.RegularExpressions.Regex.Match(
            active, $"\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s*:\\s*\"([^\"]*)\"");
        return match.Success ? match.Groups[1].Value : null;
    }
}
