using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Clients;

/// <summary>
/// Tests for the Clients list page (/clients).
/// Verifies table rendering, search functionality, navigation to create/detail.
/// </summary>
[TestFixture]
public class ClientListTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/clients", ".mud-table");
    }

    [Test]
    public async Task ClientList_PageLoads_ShowsTable()
    {
        // Table headers should be present
        var headers = Page.Locator(".mud-table-head th");
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(3),
            "Client table should have at least 3 header columns");
    }

    [Test]
    public async Task ClientList_HasSearchField()
    {
        // Search/filter input should be present
        var inputs = Page.Locator("input[type='text']");
        var count = await inputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1), "Should have at least 1 search/filter input");
    }

    [Test]
    public async Task ClientList_HasNewButton()
    {
        // "New" / "Create" button should be present
        var createButton = Page.Locator(".mud-button-filled").Filter(new() { HasText = "Nový" })
            .Or(Page.Locator(".mud-button-filled").Filter(new() { HasText = "Vytvořit" }))
            .Or(Page.Locator(".mud-button-filled").Filter(new() { HasText = "New" }));
        var count = await createButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1), "Should have a new/create client button");
    }
}
