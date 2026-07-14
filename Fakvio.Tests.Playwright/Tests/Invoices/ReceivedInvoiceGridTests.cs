using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Grid behavior tests for the Received Invoices page (/received-invoices).
///
/// Regression coverage for two fixes:
/// 1. Column filters (DocumentNumber/SupplierName/Status) used to render but were
///    never sent to the API — typing into them did nothing.
/// 2. The new "Attachments" column with per-row download and the bulk
///    "Download attachments" action.
///
/// Tests follow the defensive style of InvoiceGridTests — environments without
/// seeded data report Inconclusive instead of failing.
/// </summary>
[TestFixture]
public class ReceivedInvoiceGridTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/received-invoices", "h4");
        await WaitForTableLoadAsync();
    }

    /// <summary>
    /// Regression: typing a nonexistent value into the DocumentNumber column filter
    /// must narrow the grid to zero rows — before the fix the filter was ignored
    /// server-side and the row count never changed.
    /// </summary>
    [Test]
    public async Task Grid_ColumnFilter_DocumentNumber_NarrowsResults()
    {
        var rows = Page.Locator(".mud-table-body tr");
        var totalBefore = await rows.CountAsync();
        if (totalBefore < 1)
        {
            Assert.Inconclusive("No received invoices — cannot test column filter");
            return;
        }

        var filterInputs = Page.Locator(".mud-table-head input[type='text'], .mud-table-head .mud-input input");
        if (await filterInputs.CountAsync() == 0)
        {
            Assert.Inconclusive("No column filter text inputs — skip");
            return;
        }

        // First text filter cell belongs to the DocumentNumber column
        await filterInputs.First.FillAsync("ZZZZNOEXIST");
        await WaitForTableLoadAsync();

        var totalAfter = await rows.CountAsync();
        Assert.That(totalAfter, Is.LessThan(totalBefore),
            "DocumentNumber column filter must narrow results (was a dead filter before the fix)");

        await filterInputs.First.ClearAsync();
        await WaitForTableLoadAsync();

        var totalRestored = await rows.CountAsync();
        Assert.That(totalRestored, Is.GreaterThanOrEqualTo(totalBefore),
            "Clearing the column filter should restore results");
    }

    [Test]
    public async Task Grid_ColumnSort_DocumentNumber_ChangesOrder()
    {
        var rows = Page.Locator(".mud-table-body tr");
        if (await rows.CountAsync() < 2)
        {
            Assert.Inconclusive("Less than 2 received invoices — cannot verify sort");
            return;
        }

        var header = await FindColumnHeaderAsync("Číslo", "Number");
        if (header == null)
        {
            Assert.Inconclusive("DocumentNumber column header not found — skip");
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
            "Sorting by DocumentNumber should change row order");
    }

    /// <summary>
    /// The Attachments column header must be present in the grid.
    /// </summary>
    [Test]
    public async Task Grid_AttachmentsColumn_HeaderExists()
    {
        var header = await FindColumnHeaderAsync("Přílohy", "Attachments");
        Assert.That(header, Is.Not.Null, "Grid should contain the Attachments column");
    }

    /// <summary>
    /// Clicking the per-row attachment download icon triggers a browser download
    /// (single file or ZIP). Inconclusive when no invoice has attachments.
    /// </summary>
    [Test]
    public async Task Grid_AttachmentDownloadIcon_TriggersDownload()
    {
        // Download icons render only for rows with AttachmentCount > 0 (inside a badge)
        var downloadButtons = Page.Locator(".mud-table-body .mud-badge-wrapper .mud-icon-button");
        if (await downloadButtons.CountAsync() == 0)
        {
            Assert.Inconclusive("No received invoice with attachments — cannot test download");
            return;
        }

        var downloadTask = Page.WaitForDownloadAsync(new() { Timeout = 15_000 });
        await downloadButtons.First.ClickAsync();

        try
        {
            var download = await downloadTask;
            Assert.That(download.SuggestedFilename, Is.Not.Empty,
                "Attachment download should produce a file");
        }
        catch (TimeoutException)
        {
            Assert.Fail("Clicking the attachment download icon did not trigger a download");
        }
    }

    /// <summary>
    /// Selecting rows shows the bulk toolbar including the "Download attachments" button.
    /// </summary>
    [Test]
    public async Task Grid_BulkToolbar_ContainsDownloadAttachmentsButton()
    {
        var checkboxes = Page.Locator(".mud-table-body .mud-checkbox-input, .mud-table-body input[type='checkbox']");
        if (await checkboxes.CountAsync() == 0)
        {
            Assert.Inconclusive("No selectable rows — skip");
            return;
        }

        await checkboxes.First.ClickAsync();

        var bulkDownload = BilingualButton("Stáhnout přílohy", "Download attachments");
        try
        {
            await bulkDownload.First.WaitForAsync(new() { Timeout = 5000 });
            await Expect(bulkDownload.First).ToBeVisibleAsync();
        }
        catch (TimeoutException)
        {
            Assert.Fail("Bulk toolbar should contain the 'Download attachments' button");
        }
        finally
        {
            // Deselect to leave the page in a clean state
            await checkboxes.First.ClickAsync();
        }
    }
}
