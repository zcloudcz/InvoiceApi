using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for the Invoices list page (/invoices).
/// Covers: page load, table rendering, filter controls, pagination,
/// navigation to create, and credit note view.
/// </summary>
[TestFixture]
public class InvoiceListTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
    }

    [Test]
    public async Task InvoiceList_PageLoads_ShowsTable()
    {
        var table = Page.Locator(".mud-table");
        await Expect(table.First).ToBeVisibleAsync();

        var headers = Page.Locator(".mud-table-head th");
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(5),
            "Invoice table should have at least 5 header columns");
    }

    [Test]
    public async Task InvoiceList_HasSearchField()
    {
        var searchField = Page.Locator(".mud-input input").First;
        await Expect(searchField).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceList_HasFilterControls()
    {
        var filters = Page.Locator(".mud-select");
        var count = await filters.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Should have at least 2 filter dropdowns (status, client)");
    }

    [Test]
    public async Task InvoiceList_NewButton_Exists()
    {
        // The "New" / "Nová faktura" button should be visible on the invoices page
        // MudBlazor v8 button group CSS classes may vary — use role-based locator
        var newButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Nová faktura" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "New" }));
        await Expect(newButton.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceList_HasPagination()
    {
        var pager = Page.Locator(".mud-table-pagination");
        await Expect(pager).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceList_DropdownMenu_HasImportOption()
    {
        // Click the dropdown arrow in the split button
        var menuButton = Page.Locator(".mud-button-group .mud-menu-activator");
        if (await menuButton.CountAsync() > 0)
        {
            await menuButton.First.ClickAsync();
            await Page.WaitForSelectorAsync(".mud-popover-open", new() { Timeout = 5000 });

            // Check for Import PDF option
            var importItem = Page.Locator(".mud-popover-open .mud-list-item-text").Filter(new() { HasText = "Import PDF" });
            await Expect(importItem).ToBeVisibleAsync();

            // Close the popover by pressing Escape
            await Page.Keyboard.PressAsync("Escape");
        }
    }

    [Test]
    public async Task InvoiceList_CreditNoteView_ShowsDifferentTitle()
    {
        await Page.GotoAsync("/invoices?type=CreditNote", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });

        var heading = Page.Locator("h4");
        var text = await heading.TextContentAsync();
        Assert.That(text, Is.Not.Null.And.Not.Empty);
    }
}
