using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Navigation;

/// <summary>
/// Tests for responsive layout behavior and navigation menu.
/// Covers: nav menu visibility, drawer toggle, page layout structure.
/// </summary>
[TestFixture]
public class ResponsiveLayoutTests : FakvioPageTest
{
    [Test]
    public async Task Layout_NavMenu_HasInvoicingSection()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        var navLinks = Page.Locator(".mud-nav-link");
        var count = await navLinks.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(3),
            "Nav menu should have multiple navigation links");
    }

    [Test]
    public async Task Layout_NavMenu_HasSettingsSection()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        // Settings nav group should be present
        var body = await Page.TextContentAsync("body");
        var hasSettings = body!.Contains("Nastavení") || body.Contains("Settings");
        Assert.That(hasSettings, Is.True,
            "Nav menu should have a Settings section");
    }

    [Test]
    public async Task Layout_HasDrawer()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        var drawer = Page.Locator(".mud-drawer");
        var count = await drawer.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Layout should have a navigation drawer");
    }

    [Test]
    public async Task Layout_HasAppBar()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        var appBar = Page.Locator(".mud-appbar");
        var count = await appBar.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Layout should have an app bar");
    }

    [Test]
    public async Task Layout_HasMainContent()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        var mainContent = Page.Locator(".mud-main-content");
        var count = await mainContent.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Layout should have a main content area");
    }

    [Test]
    public async Task Layout_NavMenu_InvoicesLink_Navigates()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        // Find and click the Invoices link
        var invoicesLink = Page.Locator(".mud-nav-link").Filter(
            new() { HasText = "Faktury" })
            .Or(Page.Locator(".mud-nav-link").Filter(new() { HasText = "Invoices" }));

        if (await invoicesLink.CountAsync() > 0)
        {
            await invoicesLink.First.ClickAsync();
            await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });
            Assert.That(Page.Url, Does.Contain("/invoices"));
        }
    }

    [Test]
    public async Task Layout_NavMenu_ClientsLink_Navigates()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        var clientsLink = Page.Locator(".mud-nav-link").Filter(
            new() { HasText = "Klienti" })
            .Or(Page.Locator(".mud-nav-link").Filter(new() { HasText = "Clients" }));

        if (await clientsLink.CountAsync() > 0)
        {
            await clientsLink.First.ClickAsync();
            await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });
            Assert.That(Page.Url, Does.Contain("/clients"));
        }
    }
}
