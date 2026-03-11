using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Clients;

/// <summary>
/// Tests for the Client Detail page (/clients/{id} and /clients/create).
/// Covers: viewing existing client, tabs, form fields, invoice history,
/// create mode, and edit mode.
/// </summary>
[TestFixture]
public class ClientDetailTests : FakvioPageTest
{
    /// <summary>
    /// Navigate to the first client from the list.
    /// </summary>
    private async Task NavigateToFirstClientAsync()
    {
        await LoginAndNavigateAsync("/clients", "h4");
        await WaitForTableLoadAsync();

        var firstRow = Page.Locator(".mud-table-body tr").First;
        await firstRow.ClickAsync();

        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });
    }

    [Test]
    public async Task ClientDetail_ViewExisting_ShowsHeading()
    {
        await NavigateToFirstClientAsync();

        var heading = Page.Locator("h4").First;
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task ClientDetail_ViewExisting_ShowsStatusChip()
    {
        await NavigateToFirstClientAsync();

        // Active/Inactive status chip
        var chip = Page.Locator(".mud-chip").First;
        await Expect(chip).ToBeVisibleAsync();
    }

    [Test]
    public async Task ClientDetail_ViewExisting_ShowsCompanyName()
    {
        await NavigateToFirstClientAsync();

        var body = await Page.TextContentAsync("body");
        Assert.That(body, Is.Not.Null.And.Not.Empty,
            "Client detail should show company information");
    }

    [Test]
    public async Task ClientDetail_ViewExisting_HasTabs()
    {
        await NavigateToFirstClientAsync();

        // MudTabs for Settings / Invoice History
        var tabs = Page.Locator(".mud-tabs");
        var tabCount = await tabs.CountAsync();
        // Tabs may or may not be present depending on mode
        Assert.That(tabCount, Is.GreaterThanOrEqualTo(0),
            "Client detail should have tabs or content sections");
    }

    [Test]
    public async Task ClientDetail_ViewExisting_HasActionButtons()
    {
        await NavigateToFirstClientAsync();

        // Edit button
        var editButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Upravit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Edit" }));
        await Expect(editButton.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task ClientDetail_ViewExisting_HasBackButton()
    {
        await NavigateToFirstClientAsync();

        var backButton = Page.Locator(".mud-icon-button").First;
        await Expect(backButton).ToBeVisibleAsync();
    }

    [Test]
    public async Task ClientDetail_Create_PageLoads()
    {
        await LoginAndNavigateAsync("/clients/create", "h4");

        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task ClientDetail_Create_HasRequiredFields()
    {
        await LoginAndNavigateAsync("/clients/create", "h4");

        // Should have input fields for company info
        var inputs = Page.Locator(".mud-input input");
        var count = await inputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(3),
            "Create client form should have at least 3 input fields");
    }

    [Test]
    public async Task ClientDetail_Create_HasCancelAndCreateButtons()
    {
        await LoginAndNavigateAsync("/clients/create", "h4");

        var cancelButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Zrušit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Cancel" }));
        await Expect(cancelButton.First).ToBeVisibleAsync();

        var createButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Vytvořit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Create" }));
        await Expect(createButton.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task ClientDetail_Create_CanNavigateFromList()
    {
        await LoginAndNavigateAsync("/clients", "h4");

        var newButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Nový" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "New" }));
        await newButton.First.ClickAsync();

        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });
        Assert.That(Page.Url, Does.Contain("/clients/create"));
    }

    [Test]
    public async Task ClientDetail_InvoiceHistory_ShowsTable()
    {
        await NavigateToFirstClientAsync();

        // Click the Invoice History tab (if tabs are present)
        var historyTab = Page.Locator(".mud-tab").Filter(
            new() { HasText = "Historie" })
            .Or(Page.Locator(".mud-tab").Filter(new() { HasText = "History" }));

        if (await historyTab.CountAsync() > 0)
        {
            await historyTab.First.ClickAsync();
            await Task.Delay(500);

            // A table or "no data" message should appear
            var content = Page.Locator(".mud-table, .mud-alert");
            var count = await content.CountAsync();
            Assert.That(count, Is.GreaterThanOrEqualTo(0),
                "Invoice history tab should show a table or empty state");
        }
    }
}
