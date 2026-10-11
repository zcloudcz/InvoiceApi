using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for the Received Invoices list page (/received-invoices).
/// Verifies table rendering, filter controls, import button, and navigation.
/// </summary>
[TestFixture]
public class ReceivedInvoiceListTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/received-invoices", ".mud-table");
    }

    [Test]
    public async Task ReceivedInvoiceList_PageLoads_ShowsTable()
    {
        // Table with header columns should be present
        var headers = Page.Locator(".mud-table-head th");
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(5),
            "Received invoice table should have at least 5 header columns");
    }

    [Test]
    public async Task ReceivedInvoiceList_HasNewButton()
    {
        // "New" button should be present
        var newButton = Page.Locator(".mud-button-filled").First;
        await Expect(newButton).ToBeVisibleAsync();
    }

    /// <summary>
    /// The list is the unified invoice grid: Import PDF lives in the dropdown next to the
    /// "New" button (split button), there is no separate import button any more.
    /// </summary>
    [Test]
    public async Task ReceivedInvoiceList_ImportEntry_IsInTheDropdown()
    {
        await Page.Locator(".mud-button-group-root .mud-menu button").First.ClickAsync();

        var importItem = Page.Locator(".mud-popover-open").GetByText("Import PDF");
        await Expect(importItem).ToBeVisibleAsync();

        // Received invoices have no "create from template" entry
        // The popover is open, so a missing entry is a real absence, not a not-yet-rendered one.
        await Expect(Page.Locator(".mud-popover-open").GetByText("šablon")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task ReceivedInvoiceList_ImportEntry_NavigatesToImport()
    {
        await Page.Locator(".mud-button-group-root .mud-menu button").First.ClickAsync();
        await Page.Locator(".mud-popover-open").GetByText("Import PDF").ClickAsync();

        // Should navigate to the import page
        await Page.WaitForURLAsync("**/received-invoices/import**", new() { Timeout = 10_000 });
        Assert.That(Page.Url, Does.Contain("/received-invoices/import"));
    }

    [Test]
    public async Task ReceivedInvoiceList_HasSearchField_AndNoPageLevelStatusDropdown()
    {
        // Global search field in the grid toolbar
        var searchInputs = Page.Locator(".mud-input input");
        var count = await searchInputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1), "Should have a search input");

        // The old page-level status dropdown is gone: status is filtered in the column header only.
        var pageLevelSelects = Page.Locator("h4 ~ .mud-paper .mud-select");
        Assert.That(await pageLevelSelects.CountAsync(), Is.EqualTo(0),
            "Status is filtered in the Status column header, not by a page-level dropdown");
    }

    [Test]
    public async Task ReceivedInvoiceList_HasPagination()
    {
        var pager = Page.Locator(".mud-table-pagination");
        await Expect(pager).ToBeVisibleAsync();
    }
}
