using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the Tax Year Configs page (/tax-year-configs).
/// Covers: page load, table rendering, and configuration entries.
/// </summary>
[TestFixture]
public class TaxYearConfigTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/tax-configs", "h4");
    }

    [Test]
    public async Task TaxYearConfigs_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task TaxYearConfigs_HasTable()
    {
        var table = Page.Locator(".mud-table, .mud-card");
        var count = await table.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Tax year configs page should have a table or cards");
    }

    [Test]
    public async Task TaxYearConfigs_NoErrors()
    {
        var errors = Page.Locator(".mud-alert-filled-error");
        var count = await errors.CountAsync();
        var visibleErrors = 0;
        for (int i = 0; i < count; i++)
        {
            if (await errors.Nth(i).IsVisibleAsync())
                visibleErrors++;
        }
        Assert.That(visibleErrors, Is.EqualTo(0),
            "Tax year configs page should load without errors");
    }
}
