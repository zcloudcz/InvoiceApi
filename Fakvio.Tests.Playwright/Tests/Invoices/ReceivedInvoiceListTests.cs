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

    [Test]
    public async Task ReceivedInvoiceList_HasImportButton()
    {
        // Import PDF button should be present (outlined variant)
        var importButton = Page.Locator(".mud-button-outlined").Filter(new() { HasText = "Import PDF" });
        await Expect(importButton).ToBeVisibleAsync();
    }

    [Test]
    public async Task ReceivedInvoiceList_ImportButton_NavigatesToImport()
    {
        // Click Import PDF button
        var importButton = Page.Locator(".mud-button-outlined").Filter(new() { HasText = "Import PDF" });
        await importButton.ClickAsync();

        // Should navigate to the import page
        await Page.WaitForURLAsync("**/received-invoices/import**", new() { Timeout = 10_000 });
        Assert.That(Page.Url, Does.Contain("/received-invoices/import"));
    }

    [Test]
    public async Task ReceivedInvoiceList_HasFilterControls()
    {
        // Search field
        var searchInputs = Page.Locator(".mud-input input");
        var count = await searchInputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1), "Should have search/filter inputs");

        // Date pickers
        var datePickers = Page.Locator(".mud-picker");
        var dateCount = await datePickers.CountAsync();
        Assert.That(dateCount, Is.GreaterThanOrEqualTo(2), "Should have 2 date pickers (from/to)");
    }

    [Test]
    public async Task ReceivedInvoiceList_HasPagination()
    {
        var pager = Page.Locator(".mud-table-pagination");
        await Expect(pager).ToBeVisibleAsync();
    }
}
