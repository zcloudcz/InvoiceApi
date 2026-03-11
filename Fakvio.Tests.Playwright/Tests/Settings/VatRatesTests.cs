using Fakvio.Tests.Playwright.Infrastructure;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the VAT Rates settings page (/vat-rates).
/// The page displays a table of VAT rates with edit/delete actions.
/// </summary>
[TestFixture]
public class VatRatesTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/vat-rates", ".mud-table");
    }

    [Test]
    public async Task VatRates_PageLoads_ShowsTable()
    {
        // VAT rates table should be visible
        var table = Page.Locator(".mud-table");
        await Expect(table).ToBeVisibleAsync();

        // Should have header columns
        var headers = Page.Locator(".mud-table-head th");
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2), "VAT rates table should have at least 2 columns");
    }

    [Test]
    public async Task VatRates_HasActionButtons()
    {
        // Edit and/or delete buttons should be present if there are rows
        var rows = Page.Locator(".mud-table-body tr");
        var rowCount = await rows.CountAsync();

        if (rowCount > 0)
        {
            // At least one action button should be present in the first row
            var actionButtons = rows.First.Locator("button");
            var buttonCount = await actionButtons.CountAsync();
            Assert.That(buttonCount, Is.GreaterThan(0), "Table rows should have action buttons");
        }
    }
}
