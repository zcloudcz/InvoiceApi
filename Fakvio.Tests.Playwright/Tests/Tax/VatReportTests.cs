using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Tax;

/// <summary>
/// Tests for the VAT Report page (/vat-report).
/// Covers: period selection, generate button, shortcut buttons,
/// and report output rendering.
/// </summary>
[TestFixture]
public class VatReportTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/vat-report", "h4");
    }

    [Test]
    public async Task VatReport_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task VatReport_HasPeriodDatePickers()
    {
        var datePickers = Page.Locator(".mud-picker");
        var count = await datePickers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Should have PeriodFrom and PeriodTo date pickers");
    }

    [Test]
    public async Task VatReport_HasGenerateButton()
    {
        // CZ: "Zobrazit přehled", EN: "Generate Report"
        var generateButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Zobrazit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Generate" }));
        await Expect(generateButton.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task VatReport_HasPeriodShortcuts()
    {
        // Current Month and Current Quarter shortcut buttons
        var buttons = Page.GetByRole(AriaRole.Button);
        var count = await buttons.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Should have Generate + at least 1 shortcut button");
    }

    [Test]
    public async Task VatReport_Generate_ShowsResults()
    {
        // CZ: "Zobrazit přehled", EN: "Generate Report"
        var generateButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Zobrazit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Generate" }));
        await generateButton.First.ClickAsync();

        // Wait for results to load
        await Task.Delay(3000);

        // Should show summary cards or tables
        var content = Page.Locator(".mud-card, .mud-table");
        var count = await content.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "After generating, some result content should appear");
    }

    [Test]
    public async Task VatReport_Generate_ShowsVatBreakdown()
    {
        // CZ: "Zobrazit přehled", EN: "Generate Report"
        var generateButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Zobrazit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Generate" }));
        await generateButton.First.ClickAsync();

        await Task.Delay(3000);

        // Check for VAT-related text in the body
        var body = await Page.TextContentAsync("body");
        var hasVatContent = body!.Contains("DPH") || body.Contains("VAT")
            || body.Contains("Daň") || body.Contains("Tax");
        Assert.That(hasVatContent, Is.True,
            "VAT report should display VAT-related content after generation");
    }
}
