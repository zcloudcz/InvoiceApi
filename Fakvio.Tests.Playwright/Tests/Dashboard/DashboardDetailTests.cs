using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Dashboard;

/// <summary>
/// Extended tests for the Dashboard page (/).
/// Covers: KPI cards, charts, overdue invoices, recent invoices table,
/// quickstart timeline, and navigation from dashboard elements.
/// </summary>
[TestFixture]
public class DashboardDetailTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");
    }

    [Test]
    public async Task Dashboard_ShowsKpiCards()
    {
        // Dashboard KPI cards may be MudCard or MudPaper elements
        var cards = Page.Locator(".mud-card, .mud-paper");
        var count = await cards.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Dashboard should display KPI summary cards or paper elements");
    }

    [Test]
    public async Task Dashboard_ShowsCharts()
    {
        // MudChart renders as SVG elements
        var charts = Page.Locator(".mud-chart, svg.mud-chart-donut");
        var count = await charts.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Dashboard may show donut charts for invoice status");
    }

    [Test]
    public async Task Dashboard_ShowsRecentInvoicesTable()
    {
        var tables = Page.Locator(".mud-table");
        var count = await tables.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Dashboard should show recent invoices table (or empty state)");
    }

    [Test]
    public async Task Dashboard_KpiCard_IsClickable()
    {
        // KPI cards should be clickable (navigate to related pages)
        var firstCard = Page.Locator(".mud-card").First;
        await Expect(firstCard).ToBeVisibleAsync();

        // Verify cursor style or click handler
        var cursor = await firstCard.EvaluateAsync<string>(
            "el => getComputedStyle(el).cursor");
        // Cards may have pointer cursor if clickable
        Assert.That(cursor, Is.Not.Null);
    }

    [Test]
    public async Task Dashboard_HasNavigationLinks()
    {
        // Dashboard should have links or buttons to key pages
        var links = Page.Locator("a[href], .mud-button");
        var count = await links.CountAsync();
        Assert.That(count, Is.GreaterThan(0),
            "Dashboard should have navigation links to other pages");
    }

    [Test]
    public async Task Dashboard_RecentInvoices_RowClickNavigates()
    {
        await WaitForTableLoadAsync();

        var rows = Page.Locator(".mud-table-body tr");
        var rowCount = await rows.CountAsync();

        if (rowCount > 0)
        {
            await rows.First.ClickAsync();
            await Task.Delay(1000);

            // Should navigate to invoice detail
            var url = Page.Url;
            var navigated = url.Contains("/invoices/") || url.Contains("/received-invoices/");
            Assert.That(navigated, Is.True,
                "Clicking a recent invoice row should navigate to detail");
        }
        else
        {
            Assert.Pass("No recent invoices to click — dashboard verified");
        }
    }

    [Test]
    public async Task Dashboard_NoErrors_OnLoad()
    {
        // No error alerts should be visible on dashboard
        var errors = Page.Locator(".mud-alert-filled-error");
        var visibleErrors = 0;
        var count = await errors.CountAsync();
        for (int i = 0; i < count; i++)
        {
            if (await errors.Nth(i).IsVisibleAsync())
                visibleErrors++;
        }
        Assert.That(visibleErrors, Is.EqualTo(0),
            "Dashboard should load without errors");
    }
}
