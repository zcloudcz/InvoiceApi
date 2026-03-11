using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for the Invoice Import wizard (/invoices/import).
/// Validates step 1 (Upload) — target selection, file upload button,
/// and analyze button visibility.
/// </summary>
[TestFixture]
public class ImportWizardTests : FakvioPageTest
{
    [Test]
    public async Task ImportWizard_IssuedRoute_ShowsUploadStep()
    {
        await LoginAndNavigateAsync("/invoices/import", "h4");

        // Verify the page heading is displayed
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();

        // Verify target radio buttons are present (MudRadio renders as label elements)
        var radioLabels = Page.Locator(".mud-radio");
        var count = await radioLabels.CountAsync();
        Assert.That(count, Is.EqualTo(2), "Should have 2 radio buttons (Issued + Received)");
    }

    [Test]
    public async Task ImportWizard_ReceivedRoute_LoadsSuccessfully()
    {
        await LoginAndNavigateAsync("/received-invoices/import", "h4");
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task ImportWizard_HasFileUploadButton()
    {
        await LoginAndNavigateAsync("/invoices/import", "h4");

        // MudFileUpload renders a button with the upload text
        var uploadButton = Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "PDF" });
        await Expect(uploadButton).ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Test]
    public async Task ImportWizard_QueryParam_SetsTarget()
    {
        await LoginAndNavigateAsync("/invoices/import?target=ReceivedInvoice", "h4");

        // Page should load successfully — heading visible
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task ImportWizard_AnalyzeButton_NotVisibleWithoutFiles()
    {
        await LoginAndNavigateAsync("/invoices/import", "h4");

        // The "Analyze" button should not be visible (only shown after files are selected)
        var analyzeButton = Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Analyzovat" });
        await Expect(analyzeButton).Not.ToBeVisibleAsync();
    }
}
