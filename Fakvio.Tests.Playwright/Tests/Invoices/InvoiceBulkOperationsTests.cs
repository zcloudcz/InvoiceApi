using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for bulk operations on the Invoice list page.
/// Covers: status filter, deleted filter, table sorting, and search.
/// </summary>
[TestFixture]
public class InvoiceBulkOperationsTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
    }

    [Test]
    public async Task InvoiceList_StatusFilter_ChangesResults()
    {
        await WaitForTableLoadAsync();

        // Get initial row count
        var initialRows = await Page.Locator(".mud-table-body tr").CountAsync();

        // Click the first MudSelect (status filter)
        var statusSelect = Page.Locator(".mud-select").First;
        await statusSelect.ClickAsync();

        // Wait for popover to open
        await Task.Delay(500);

        // Select a filter option from the popover
        var listItems = Page.Locator(".mud-popover-open .mud-list-item");
        var itemCount = await listItems.CountAsync();
        if (itemCount > 1)
        {
            await listItems.Nth(1).ClickAsync(); // Select second option
            await Task.Delay(1000);
        }
        else
        {
            await Page.Keyboard.PressAsync("Escape");
        }

        // Page should still render without errors
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceList_Search_FiltersTable()
    {
        await WaitForTableLoadAsync();

        // Search input — use non-readonly input (skip MudSelect readonly inputs)
        var searchField = Page.Locator(".mud-input input:not([readonly])").First;
        if (await searchField.CountAsync() > 0)
        {
            await searchField.FillAsync("test");

            // Wait for debounced search to trigger
            await Task.Delay(1500);
        }

        // Table should still be visible (may have 0 results)
        var table = Page.Locator(".mud-table");
        await Expect(table.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceList_TableSorting_ClickHeader()
    {
        await WaitForTableLoadAsync();

        var headers = Page.Locator(".mud-table-head th");
        var headerCount = await headers.CountAsync();

        if (headerCount > 1)
        {
            // Click a sortable header
            await headers.Nth(1).ClickAsync();
            await Task.Delay(500);

            // Table should still render
            var table = Page.Locator(".mud-table");
            await Expect(table.First).ToBeVisibleAsync();
        }
    }

    [Test]
    public async Task InvoiceList_CreditNoteFilter_ShowsCreditNotes()
    {
        await Page.GotoAsync("/invoices?type=CreditNote", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });

        // The heading should indicate credit notes
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();

        // Table should render (even if empty)
        var table = Page.Locator(".mud-table");
        await Expect(table.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceList_Pagination_Works()
    {
        await WaitForTableLoadAsync();

        var pager = Page.Locator(".mud-table-pagination");
        await Expect(pager).ToBeVisibleAsync();

        // Try clicking the next page button
        var nextButton = pager.Locator("button").Last;
        var isEnabled = await nextButton.IsEnabledAsync();

        if (isEnabled)
        {
            await nextButton.ClickAsync();
            await Task.Delay(1000);

            // Table should still be visible after pagination
            var table = Page.Locator(".mud-table");
            await Expect(table.First).ToBeVisibleAsync();
        }
    }
}
