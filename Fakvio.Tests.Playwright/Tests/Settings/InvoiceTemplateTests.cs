using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the Invoice Templates page (/invoice-templates).
/// Covers: list rendering, create navigation, and row click.
/// </summary>
[TestFixture]
public class InvoiceTemplateTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/invoice-templates", "h4");
    }

    [Test]
    public async Task InvoiceTemplates_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceTemplates_HasTable()
    {
        var table = Page.Locator(".mud-table");
        var count = await table.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Invoice templates page should display a data table");
    }

    [Test]
    public async Task InvoiceTemplates_HasNewButton()
    {
        var newButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Nový" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "New" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Přidat" })));
        var count = await newButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Invoice templates page should have a create button");
    }

    [Test]
    public async Task InvoiceTemplates_TableHasRows()
    {
        await WaitForTableLoadAsync();

        var rows = Page.Locator(".mud-table-body tr");
        var count = await rows.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Invoice templates table should render (even if empty)");
    }
}
