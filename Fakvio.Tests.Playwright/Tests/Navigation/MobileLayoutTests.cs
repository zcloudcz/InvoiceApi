using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Navigation;

/// <summary>
/// Mobile viewport (375x812, iPhone-class) layout tests.
/// Guards the responsive conventions from DEVGUIDE 7.11:
/// - no horizontal page overflow,
/// - ResponsiveButton collapses to icon-only (label hidden),
/// - AppBar content fits the viewport,
/// - HideSmall grid columns are not visible.
/// </summary>
[TestFixture]
public class MobileLayoutTests : FakvioPageTest
{
    /// <summary>Same context as FakvioPageTest, but with a phone viewport.</summary>
    public override BrowserNewContextOptions ContextOptions()
    {
        var options = base.ContextOptions();
        options.ViewportSize = new ViewportSize { Width = 375, Height = 812 };
        return options;
    }

    /// <summary>Reads horizontal overflow of the whole document.</summary>
    private async Task<(int ScrollW, int ClientW)> GetDocumentWidthsAsync()
    {
        var scrollW = await Page.EvaluateAsync<int>("() => document.documentElement.scrollWidth");
        var clientW = await Page.EvaluateAsync<int>("() => document.documentElement.clientWidth");
        return (scrollW, clientW);
    }

    [Test]
    public async Task Mobile_Invoices_NoHorizontalOverflow()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        var (scrollW, clientW) = await GetDocumentWidthsAsync();
        Assert.That(scrollW, Is.LessThanOrEqualTo(clientW + 1),
            "Invoices page must not scroll horizontally on a phone viewport");
    }

    [Test]
    public async Task Mobile_Invoices_NewButton_IconOnly()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        var label = Page.Locator(".btn-responsive .btn-responsive-label").First;
        if (await label.CountAsync() == 0)
        {
            Assert.Inconclusive("No ResponsiveButton on page — skip");
            return;
        }

        // Label hidden by CSS below 600px; the button (with its icon) stays visible
        Assert.That(await label.IsVisibleAsync(), Is.False,
            "ResponsiveButton label must be hidden on phones (icon-only '+')");
        Assert.That(await Page.Locator(".btn-responsive").First.IsVisibleAsync(), Is.True,
            "ResponsiveButton itself must stay visible");
    }

    [Test]
    public async Task Mobile_AppBar_Fits()
    {
        await LoginAndNavigateAsync("/invoices", "h4");

        var scrollW = await Page.EvaluateAsync<int>(
            "() => document.querySelector('.mud-appbar .mud-toolbar')?.scrollWidth ?? 0");
        var clientW = await Page.EvaluateAsync<int>(
            "() => document.querySelector('.mud-appbar .mud-toolbar')?.clientWidth ?? 0");

        Assert.That(scrollW, Is.LessThanOrEqualTo(clientW + 1),
            "AppBar content must fit the phone viewport (no clipped icons)");
    }

    [Test]
    public async Task Mobile_ReceivedInvoices_NoHorizontalOverflow()
    {
        await LoginAndNavigateAsync("/received-invoices", "h4");
        await WaitForTableLoadAsync();

        var (scrollW, clientW) = await GetDocumentWidthsAsync();
        Assert.That(scrollW, Is.LessThanOrEqualTo(clientW + 1),
            "Received invoices page must not scroll horizontally on a phone viewport");
    }

    [Test]
    public async Task Mobile_Grid_HideSmallColumns_NotVisible()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        var hideCells = Page.Locator(".mud-table-cell-hide");
        var count = await hideCells.CountAsync();
        if (count == 0)
        {
            Assert.Inconclusive("No HideSmall columns rendered — skip");
            return;
        }

        for (var i = 0; i < count; i++)
        {
            Assert.That(await hideCells.Nth(i).IsVisibleAsync(), Is.False,
                "HideSmall column cells must be hidden below 600px");
        }
    }
}
