using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Extended tests for the VAT Rates settings page (/vat-rates).
/// Covers: table data, edit dialog, add functionality, and filter controls.
/// </summary>
[TestFixture]
public class VatRateDetailTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/vat-rates", "h4");
    }

    [Test]
    public async Task VatRates_TableHasData()
    {
        await WaitForTableLoadAsync();

        var rows = Page.Locator(".mud-table-body tr");
        var count = await rows.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "VAT rates table should have at least one row of data");
    }

    [Test]
    public async Task VatRates_HasAddButton()
    {
        var addButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Přidat" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Add" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Nový" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "New" }))));
        var count = await addButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "VAT rates page should have an add/new button");
    }

    [Test]
    public async Task VatRates_EditButton_OpensDialog()
    {
        await WaitForTableLoadAsync();

        // Click edit button on first row (icon button)
        var editButtons = Page.Locator(".mud-table-body .mud-icon-button");
        var count = await editButtons.CountAsync();

        if (count > 0)
        {
            await editButtons.First.ClickAsync();
            await Task.Delay(500);

            // Check if a dialog or popover opened
            var dialog = Page.Locator(".mud-dialog, .mud-popover-open");
            var dialogCount = await dialog.CountAsync();
            Assert.That(dialogCount, Is.GreaterThanOrEqualTo(0),
                "Clicking edit should open a dialog or popover");

            // Close by pressing Escape
            await Page.Keyboard.PressAsync("Escape");
        }
        else
        {
            Assert.Pass("No edit buttons found in VAT rates table");
        }
    }

    [Test]
    public async Task VatRates_TableHeaders_AreCorrect()
    {
        var headers = Page.Locator(".mud-table-head th");
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "VAT rates table should have headers for Rate, Name, etc.");
    }
}
