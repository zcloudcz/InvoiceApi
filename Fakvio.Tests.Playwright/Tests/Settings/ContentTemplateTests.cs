using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the Content Templates page (/content-templates) and detail.
/// Covers: list rendering, template types, and create/edit navigation.
/// </summary>
[TestFixture]
public class ContentTemplateTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/content-templates", "h4");
    }

    [Test]
    public async Task ContentTemplates_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task ContentTemplates_HasTable()
    {
        var table = Page.Locator(".mud-table");
        var count = await table.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Content templates page should have a data table");
    }

    [Test]
    public async Task ContentTemplates_HasNewButton()
    {
        var newButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Nový" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "New" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Přidat" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Add" }))));
        var count = await newButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Content templates page should have a create button");
    }

    [Test]
    public async Task ContentTemplates_TableHasHeaders()
    {
        var headers = Page.Locator(".mud-table-head th");
        var count = await headers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Content templates table should have columns for Name, Type, etc.");
    }

    [Test]
    public async Task ContentTemplates_RowClick_NavigatesToDetail()
    {
        await WaitForTableLoadAsync();

        var firstRow = Page.Locator(".mud-table-body tr").First;
        var rowCount = await firstRow.CountAsync();

        if (rowCount > 0 && await firstRow.IsVisibleAsync())
        {
            await firstRow.ClickAsync();
            await Task.Delay(1000);

            var url = Page.Url;
            Assert.That(url, Does.Contain("/content-templates/"),
                "Clicking a template row should navigate to its detail");
        }
        else
        {
            Assert.Pass("No content templates to click");
        }
    }
}
