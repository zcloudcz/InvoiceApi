using Fakvio.Tests.Playwright.Infrastructure;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Navigation;

/// <summary>
/// Tests for the navigation menu structure.
/// </summary>
[TestFixture]
public class NavMenuTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/");
    }

    [Test]
    public async Task NavMenu_DashboardLink_IsVisible()
    {
        var dashboardLink = Page.Locator(".mud-nav-link").Filter(new() { HasText = "Dashboard" });
        await Expect(dashboardLink).ToBeVisibleAsync(new() { Timeout = 10_000 });
    }

    [Test]
    public async Task NavMenu_HasMultipleLinks()
    {
        // The nav menu should contain multiple navigation links
        var links = Page.Locator(".mud-nav-link");
        var count = await links.CountAsync();
        Assert.That(count, Is.GreaterThan(3), "Nav menu should contain multiple navigation links");
    }

    [Test]
    public async Task NavMenu_SettingsGroup_IsPresent()
    {
        // The Settings nav group should be present (text "Nastavení")
        var settingsGroup = Page.Locator(".mud-nav-group").Filter(new() { HasText = "Nastavení" });
        await Expect(settingsGroup).ToBeVisibleAsync(new() { Timeout = 5000 });
    }
}
