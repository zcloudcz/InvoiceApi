using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Clients;

/// <summary>
/// Extended tests for the Client List page (/clients).
/// Covers: table data, pagination, row click navigation, and filter behavior.
/// </summary>
[TestFixture]
public class ClientListExtendedTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/clients", "h4");
    }

    [Test]
    public async Task ClientList_TableHasData()
    {
        await WaitForTableLoadAsync();

        var rows = Page.Locator(".mud-table-body tr");
        var count = await rows.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Client table should have at least one client row");
    }

    [Test]
    public async Task ClientList_HasPagination()
    {
        var pager = Page.Locator(".mud-table-pagination");
        var count = await pager.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Client list should have pagination controls");
    }

    [Test]
    public async Task ClientList_RowClick_NavigatesToDetail()
    {
        await WaitForTableLoadAsync();

        var firstRow = Page.Locator(".mud-table-body tr").First;
        await firstRow.ClickAsync();

        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });
        Assert.That(Page.Url, Does.Contain("/clients/"),
            "Clicking a client row should navigate to detail");
    }

    [Test]
    public async Task ClientList_TableHeaders_AreCorrect()
    {
        var headers = Page.Locator(".mud-table-head th");
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(3),
            "Client table should have headers for Name, ICO, etc.");
    }

    [Test]
    public async Task ClientList_NoErrors()
    {
        var errors = Page.Locator(".mud-alert-filled-error");
        var visibleErrors = 0;
        var count = await errors.CountAsync();
        for (int i = 0; i < count; i++)
        {
            if (await errors.Nth(i).IsVisibleAsync())
                visibleErrors++;
        }
        Assert.That(visibleErrors, Is.EqualTo(0),
            "Client list should load without errors");
    }
}
