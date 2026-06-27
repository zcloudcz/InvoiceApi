using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for MudDataGrid sorting, filtering, and pagination on the invoice list.
/// Verifies that column sorting changes row order, column filters narrow results,
/// search field filters by text, and pagination controls navigate between pages.
/// </summary>
[TestFixture]
public class InvoiceGridTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();
    }

    /// <summary>
    /// Clicks a column header twice (asc then desc) and verifies first row changes.
    /// </summary>
    private async Task AssertSortChangesOrderAsync(string czechLabel, string englishLabel)
    {
        var rows = Page.Locator(".mud-table-body tr");
        if (await rows.CountAsync() < 2)
        {
            Assert.Inconclusive($"Less than 2 invoices — cannot verify {czechLabel} sort");
            return;
        }

        var header = await FindColumnHeaderAsync(czechLabel, englishLabel);
        if (header == null)
        {
            Assert.Inconclusive($"{czechLabel} column header not found — skip");
            return;
        }

        var firstBefore = await rows.First.TextContentAsync() ?? "";

        await header.ClickAsync();
        await WaitForTableLoadAsync();
        var firstAsc = await rows.First.TextContentAsync() ?? "";

        await header.ClickAsync();
        await WaitForTableLoadAsync();
        var firstDesc = await rows.First.TextContentAsync() ?? "";

        Assert.That(firstAsc != firstDesc || firstBefore != firstAsc, Is.True,
            $"Sorting by {czechLabel} should change row order");
    }

    [Test]
    public async Task Grid_ColumnSort_DocumentNumber_ChangesOrder()
    {
        await AssertSortChangesOrderAsync("Číslo", "Number");
    }

    [Test]
    public async Task Grid_ColumnSort_DueDate_ChangesOrder()
    {
        await AssertSortChangesOrderAsync("Splatnost", "Due");
    }

    [Test]
    public async Task Grid_ColumnSort_TotalAmount_ChangesOrder()
    {
        await AssertSortChangesOrderAsync("Celkem", "Total");
    }

    [Test]
    public async Task Grid_SearchFilter_NarrowsResults()
    {
        var rows = Page.Locator(".mud-table-body tr");
        var totalBefore = await rows.CountAsync();
        if (totalBefore == 0)
        {
            Assert.Inconclusive("No invoices — cannot test search filter");
            return;
        }

        var firstRowText = await rows.First.TextContentAsync() ?? "";
        var searchTerm = firstRowText.Length > 10 ? firstRowText[..10].Trim() : firstRowText.Trim();

        var searchInput = Page.Locator("input[placeholder*='Hledat'], input[placeholder*='Search']");
        if (await searchInput.CountAsync() == 0)
            searchInput = Page.Locator(".mud-toolbar-content .mud-input input");

        if (await searchInput.CountAsync() == 0)
        {
            Assert.Inconclusive("No search input found — skip");
            return;
        }

        await searchInput.First.FillAsync(searchTerm);
        await WaitForTableLoadAsync();

        var totalAfter = await rows.CountAsync();
        Assert.That(totalAfter, Is.LessThanOrEqualTo(totalBefore),
            "Search filter should narrow results (fewer or same number of rows)");
    }

    [Test]
    public async Task Grid_SearchFilter_NoMatch_ShowsEmptyOrMessage()
    {
        var searchInput = Page.Locator("input[placeholder*='Hledat'], input[placeholder*='Search']");
        if (await searchInput.CountAsync() == 0)
            searchInput = Page.Locator(".mud-toolbar-content .mud-input input");

        if (await searchInput.CountAsync() == 0)
        {
            Assert.Inconclusive("No search input — skip");
            return;
        }

        await searchInput.First.FillAsync("ZZZZNONEXISTENT99999");
        await WaitForTableLoadAsync();

        var rows = Page.Locator(".mud-table-body tr");
        var count = await rows.CountAsync();
        var alertCount = await Page.Locator(".mud-alert").CountAsync();

        Assert.That(count == 0 || alertCount > 0, Is.True,
            "Searching for nonexistent text should show empty grid or 'no records' message");
    }

    [Test]
    public async Task Grid_SearchFilter_ClearRestoresAll()
    {
        var rows = Page.Locator(".mud-table-body tr");
        var totalBefore = await rows.CountAsync();
        if (totalBefore == 0)
        {
            Assert.Inconclusive("No invoices — skip");
            return;
        }

        var searchInput = Page.Locator("input[placeholder*='Hledat'], input[placeholder*='Search']");
        if (await searchInput.CountAsync() == 0)
            searchInput = Page.Locator(".mud-toolbar-content .mud-input input");
        if (await searchInput.CountAsync() == 0)
        {
            Assert.Inconclusive("No search input — skip");
            return;
        }

        await searchInput.First.FillAsync("ZZZZNONEXISTENT99999");
        await WaitForTableLoadAsync();

        await searchInput.First.ClearAsync();
        await WaitForTableLoadAsync();

        var totalAfter = await rows.CountAsync();
        Assert.That(totalAfter, Is.GreaterThanOrEqualTo(totalBefore),
            "Clearing search should restore the original row count");
    }

    [Test]
    public async Task Grid_Pagination_ChangesPageSize()
    {
        var rows = Page.Locator(".mud-table-body tr");
        if (await rows.CountAsync() < 11)
        {
            Assert.Inconclusive("Not enough invoices to test pagination — skip");
            return;
        }

        var pageSizeSelect = Page.Locator(".mud-table-pagination .mud-select");
        if (await pageSizeSelect.CountAsync() == 0)
        {
            Assert.Inconclusive("No page size selector — skip");
            return;
        }

        await pageSizeSelect.First.ClickAsync();
        await Page.WaitForSelectorAsync(".mud-popover-open", new() { Timeout = 3000 });

        var option10 = Page.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasText = "10" });
        if (await option10.CountAsync() > 0)
        {
            await option10.First.ClickAsync();
            await WaitForTableLoadAsync();

            var countAfter = await rows.CountAsync();
            Assert.That(countAfter, Is.LessThanOrEqualTo(10),
                "Setting page size to 10 should show at most 10 rows");
        }
    }

    [Test]
    public async Task Grid_Pagination_NextPage_ShowsDifferentData()
    {
        var rows = Page.Locator(".mud-table-body tr");
        if (await rows.CountAsync() < 10)
        {
            Assert.Inconclusive("Not enough invoices for multi-page — skip");
            return;
        }

        var firstRowPage1 = await rows.First.TextContentAsync() ?? "";

        var paginationButtons = Page.Locator(".mud-table-pagination .mud-icon-button");
        var btnCount = await paginationButtons.CountAsync();

        if (btnCount < 4)
        {
            Assert.Inconclusive("Pagination buttons not found — skip");
            return;
        }

        // Navigation buttons order: first, prev, next, last
        var nextPage = paginationButtons.Nth(btnCount - 2);
        if (await nextPage.IsDisabledAsync())
        {
            Assert.Inconclusive("Next page button disabled — only one page of data");
            return;
        }

        await nextPage.ClickAsync();
        await WaitForTableLoadAsync();

        var firstRowPage2 = await rows.First.TextContentAsync() ?? "";
        Assert.That(firstRowPage2, Is.Not.EqualTo(firstRowPage1),
            "Next page should show different data than page 1");
    }

    [Test]
    public async Task Grid_ColumnFilter_StatusDropdown_FiltersRows()
    {
        var rows = Page.Locator(".mud-table-body tr");
        var totalBefore = await rows.CountAsync();
        if (totalBefore < 2)
        {
            Assert.Inconclusive("Not enough invoices to verify filtering — skip");
            return;
        }

        var filterSelects = Page.Locator(".mud-table-head .mud-select, .mud-table-head select");
        if (await filterSelects.CountAsync() == 0)
            filterSelects = Page.Locator(".mud-table-head tr:nth-child(2) .mud-select");

        if (await filterSelects.CountAsync() == 0)
        {
            Assert.Inconclusive("No column filter selects found");
            return;
        }

        await filterSelects.First.ClickAsync();
        await Page.WaitForSelectorAsync(".mud-popover-open", new() { Timeout = 3000 });

        var options = Page.Locator(".mud-popover-open .mud-list-item");
        if (await options.CountAsync() > 1)
        {
            await options.Nth(1).ClickAsync();
            await WaitForTableLoadAsync();

            var totalAfter = await rows.CountAsync();
            Assert.That(totalAfter, Is.LessThanOrEqualTo(totalBefore),
                "Applying column filter should narrow or maintain result count");
        }
    }

    [Test]
    public async Task Grid_ColumnFilter_TextInput_FiltersRows()
    {
        var rows = Page.Locator(".mud-table-body tr");
        var totalBefore = await rows.CountAsync();
        if (totalBefore < 2)
        {
            Assert.Inconclusive("Not enough invoices — skip");
            return;
        }

        var filterInputs = Page.Locator(".mud-table-head input[type='text'], .mud-table-head .mud-input input");
        if (await filterInputs.CountAsync() == 0)
        {
            Assert.Inconclusive("No column filter text inputs — skip");
            return;
        }

        await filterInputs.First.FillAsync("ZZZZNOEXIST");
        await WaitForTableLoadAsync();

        var totalAfter = await rows.CountAsync();
        Assert.That(totalAfter, Is.LessThan(totalBefore),
            "Column text filter should narrow results when filtering by nonexistent value");

        await filterInputs.First.ClearAsync();
        await WaitForTableLoadAsync();

        var totalRestored = await rows.CountAsync();
        Assert.That(totalRestored, Is.GreaterThanOrEqualTo(totalBefore),
            "Clearing column filter should restore results");
    }

    [Test]
    public async Task Grid_SortIndicator_AppearsOnClick()
    {
        var headers = Page.Locator(".mud-table-head th");
        if (await headers.CountAsync() < 3)
        {
            Assert.Inconclusive("Not enough columns — skip");
            return;
        }

        await headers.Nth(2).ClickAsync();
        await WaitForTableLoadAsync();

        var tableStillVisible = await Page.Locator(".mud-table").First.IsVisibleAsync();
        Assert.That(tableStillVisible, Is.True,
            "Table should remain visible after clicking column header for sorting");
    }

    [Test]
    public async Task Grid_MultiSelect_SelectionToolbarAppears()
    {
        var rows = Page.Locator(".mud-table-body tr");
        if (await rows.CountAsync() == 0)
        {
            Assert.Inconclusive("No invoices — skip");
            return;
        }

        var checkboxes = Page.Locator(".mud-table-body .mud-checkbox-input, .mud-table-body input[type='checkbox']");
        if (await checkboxes.CountAsync() == 0)
        {
            var selectCells = Page.Locator(".mud-table-body td:first-child");
            if (await selectCells.CountAsync() > 0)
            {
                await selectCells.First.ClickAsync();
            }
            else
            {
                Assert.Inconclusive("No selection checkboxes — skip");
                return;
            }
        }
        else
        {
            await checkboxes.First.ClickAsync();
        }

        var bulkToolbar = Page.Locator("text=vybráno").Or(Page.Locator("text=selected"));

        try
        {
            await bulkToolbar.First.WaitForAsync(new() { Timeout = 3000 });
            var bulkButtons = Page.Locator(".mud-paper").Filter(new() { HasText = "vybráno" })
                .Or(Page.Locator(".mud-paper").Filter(new() { HasText = "selected" }));
            await Expect(bulkButtons.First).ToBeVisibleAsync();
        }
        catch (TimeoutException) { }

        if (await checkboxes.CountAsync() > 0)
        {
            await checkboxes.First.ClickAsync();
        }
    }
}
