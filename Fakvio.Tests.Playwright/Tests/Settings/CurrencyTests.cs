using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the Currencies settings page (/currencies).
/// Covers: page load, table rendering, and CRUD controls.
/// </summary>
[TestFixture]
public class CurrencyTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/currencies", "h4");
    }

    [Test]
    public async Task Currencies_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task Currencies_HasTable()
    {
        var table = Page.Locator(".mud-table");
        var count = await table.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Currencies page should have a data table");
    }

    [Test]
    public async Task Currencies_TableHasData()
    {
        await WaitForTableLoadAsync();

        var rows = Page.Locator(".mud-table-body tr");
        var count = await rows.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Currencies table should have at least CZK entry");
    }

    [Test]
    public async Task Currencies_HasActionButtons()
    {
        var buttons = Page.GetByRole(AriaRole.Button);
        var count = await buttons.CountAsync();
        Assert.That(count, Is.GreaterThan(0),
            "Currencies page should have action buttons");
    }
}
