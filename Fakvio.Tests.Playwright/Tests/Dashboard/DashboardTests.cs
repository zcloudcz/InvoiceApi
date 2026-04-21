using Fakvio.Tests.Playwright.Infrastructure;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Dashboard;

/// <summary>
/// Tests for the main dashboard page (/).
/// The dashboard shows KPI cards (invoices due this month, clients, unpaid amount),
/// recent invoices table, and quick-start timeline.
///
/// Note: Dashboard content varies based on whether the SysAdmin is impersonating
/// a company. Without impersonation, SysAdmin sees the system admin dashboard.
/// </summary>
[TestFixture]
public class DashboardTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/");
    }

    [Test]
    public async Task Dashboard_PageLoads_ShowsContent()
    {
        // The dashboard should show some MudBlazor content (cards, tables, etc.)
        // At minimum, the page title and navigation should be visible
        var mudContent = Page.Locator(".mud-card, .mud-paper, .mud-table, .mud-timeline");
        var count = await mudContent.CountAsync();
        Assert.That(count, Is.GreaterThan(0), "Dashboard should contain at least one MudBlazor card/paper/table/timeline element");
    }

    [Test]
    public async Task Dashboard_NavMenu_IsVisible()
    {
        // The left nav menu should be rendered with multiple links
        var navLinks = Page.Locator(".mud-nav-link");
        var count = await navLinks.CountAsync();
        Assert.That(count, Is.GreaterThan(0), "Nav menu should contain navigation links");
    }

    [Test]
    public async Task Dashboard_Title_IsDisplayed()
    {
        // The page should have a visible title/header
        var title = await Page.TitleAsync();
        Assert.That(title, Is.Not.Empty, "Page title should not be empty");
    }
}
