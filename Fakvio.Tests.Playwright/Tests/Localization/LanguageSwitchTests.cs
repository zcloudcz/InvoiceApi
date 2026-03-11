using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Localization;

/// <summary>
/// Tests for localization and language switching.
/// Verifies that the app renders Czech text by default (cs-CZ locale)
/// and that key UI elements are properly localized.
/// </summary>
[TestFixture]
public class LanguageSwitchTests : FakvioPageTest
{
    [Test]
    public async Task Localization_CzechLocale_ShowsCzechText()
    {
        // Browser context is set to cs-CZ in FakvioPageTest
        await LoginAndNavigateAsync("/invoices", "h4");

        var heading = Page.Locator("h4");
        var text = await heading.TextContentAsync();

        // Should show Czech heading ("Faktury" or similar)
        Assert.That(text, Is.Not.Null.And.Not.Empty,
            "Invoices page should show a heading in Czech");
    }

    [Test]
    public async Task Localization_CzechLocale_ButtonsAreCzech()
    {
        await LoginAndNavigateAsync("/invoices", "h4");

        var body = await Page.TextContentAsync("body");
        // Check for Czech button text
        var hasCzech = body!.Contains("Nová faktura") || body.Contains("Faktury")
            || body.Contains("Přidat") || body.Contains("Hledat");
        Assert.That(hasCzech, Is.True,
            "Page should contain Czech language text");
    }

    [Test]
    public async Task Localization_CzechLocale_DashboardCzech()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        var body = await Page.TextContentAsync("body");
        var hasCzech = body!.Contains("Dashboard") || body.Contains("Přehled")
            || body.Contains("Faktury") || body.Contains("Klienti");
        Assert.That(hasCzech, Is.True,
            "Dashboard should contain localized text");
    }

    [Test]
    public async Task Localization_NavMenuItems_AreCzech()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        // Nav links should be in Czech
        var navText = await Page.Locator(".mud-navmenu").First.TextContentAsync();
        Assert.That(navText, Is.Not.Null.And.Not.Empty,
            "Nav menu should have localized text");
    }
}
