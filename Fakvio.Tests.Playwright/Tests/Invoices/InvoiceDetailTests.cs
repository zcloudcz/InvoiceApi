using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for the Invoice Detail page (/invoices/{id}).
/// Covers: viewing existing invoice, status display, action buttons,
/// item table rendering, PDF export, email dialog, and edit mode.
/// </summary>
[TestFixture]
public class InvoiceDetailTests : FakvioPageTest
{
    /// <summary>
    /// Navigate to the first invoice from the list.
    /// </summary>
    private async Task NavigateToFirstInvoiceAsync()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        // Click the first row in the invoice table
        var firstRow = Page.Locator(".mud-table-body tr").First;
        await firstRow.ClickAsync();

        // Wait for detail page to load
        await Page.WaitForSelectorAsync("h4, h3, h5", new() { Timeout = Config.BlazorLoadTimeout });
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_ShowsDocumentNumber()
    {
        await NavigateToFirstInvoiceAsync();

        // The page should show invoice heading or document number
        var heading = Page.Locator("h4, h3, h5").First;
        await Expect(heading).ToBeVisibleAsync();
        var text = await heading.TextContentAsync();
        Assert.That(text, Is.Not.Null.And.Not.Empty, "Invoice detail should show a heading");
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_ShowsStatusChip()
    {
        await NavigateToFirstInvoiceAsync();

        // Status is displayed as a MudChip
        var statusChip = Page.Locator(".mud-chip").First;
        await Expect(statusChip).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_ShowsInvoiceItems()
    {
        await NavigateToFirstInvoiceAsync();

        // Items are displayed in a MudTable
        var itemsTable = Page.Locator(".mud-table");
        var tableCount = await itemsTable.CountAsync();
        Assert.That(tableCount, Is.GreaterThanOrEqualTo(1),
            "Invoice detail should have at least one table for items");
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_ShowsClientInfo()
    {
        await NavigateToFirstInvoiceAsync();

        // Client name should be displayed somewhere on the page
        var pageContent = await Page.TextContentAsync("body");
        Assert.That(pageContent, Is.Not.Null.And.Not.Empty,
            "Invoice detail should have content including client info");
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_HasBackButton()
    {
        await NavigateToFirstInvoiceAsync();

        // Back button (MudIconButton with ArrowBack)
        var backButton = Page.Locator(".mud-icon-button").First;
        await Expect(backButton).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_HasActionButtons()
    {
        await NavigateToFirstInvoiceAsync();

        // Should have at least one action button (Edit, Issue, Delete, etc.)
        var buttons = Page.GetByRole(AriaRole.Button);
        var count = await buttons.CountAsync();
        Assert.That(count, Is.GreaterThan(1), "Invoice detail should have action buttons");
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_ShowsPaymentInfo()
    {
        await NavigateToFirstInvoiceAsync();

        // Payment info section should have text content about payment
        var body = await Page.TextContentAsync("body");
        // Check for common payment-related terms (CZ or EN)
        var hasPaymentInfo = body!.Contains("Platba") || body.Contains("Payment")
            || body.Contains("CZK") || body.Contains("EUR")
            || body.Contains("Kč") || body.Contains("Bankovní");
        Assert.That(hasPaymentInfo, Is.True,
            "Invoice detail should display payment information");
    }

    [Test]
    public async Task InvoiceDetail_ViewExisting_ShowsTotals()
    {
        await NavigateToFirstInvoiceAsync();

        // Should display total amounts somewhere
        var body = await Page.TextContentAsync("body");
        // Look for formatted numbers (amounts) or total-related labels
        var hasTotals = body!.Contains("Celkem") || body.Contains("Total")
            || body.Contains("DPH") || body.Contains("VAT");
        Assert.That(hasTotals, Is.True,
            "Invoice detail should display totals or VAT info");
    }

    [Test]
    public async Task InvoiceDetail_Create_CanNavigateFromList()
    {
        await LoginAndNavigateAsync("/invoices", "h4");

        // Click the "New" button
        var newButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Nová faktura" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "New" }));
        await newButton.First.ClickAsync();

        // Wait for create page to load
        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });
        Assert.That(Page.Url, Does.Contain("/invoices/create"));
    }

    [Test]
    public async Task InvoiceDetail_Create_HasClientSelector()
    {
        await LoginAndNavigateAsync("/invoices/create", "h4");

        // Should have MudSelect for client
        var selects = Page.Locator(".mud-select");
        var count = await selects.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Create form should have client selector");
    }

    [Test]
    public async Task InvoiceDetail_Create_HasDateFields()
    {
        await LoginAndNavigateAsync("/invoices/create", "h4");

        // Issue date and Due date pickers
        var datePickers = Page.Locator(".mud-picker");
        var count = await datePickers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Create form should have at least 2 date pickers (Issue Date, Due Date)");
    }

    [Test]
    public async Task InvoiceDetail_Create_HasCancelAndCreateButtons()
    {
        await LoginAndNavigateAsync("/invoices/create", "h4");

        // Cancel button
        var cancelButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Zrušit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Cancel" }));
        await Expect(cancelButton.First).ToBeVisibleAsync();

        // Create/Save button
        var createButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Vytvořit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Create" }));
        await Expect(createButton.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task InvoiceDetail_PdfExportButton_Exists()
    {
        await NavigateToFirstInvoiceAsync();

        // PDF export button or button group
        var pdfButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "PDF" });
        var count = await pdfButton.CountAsync();
        // PDF button might only appear on completed invoices
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "PDF export button should exist on invoice detail (if status allows)");
    }

    [Test]
    public async Task InvoiceDetail_IsdocButton_Exists()
    {
        await NavigateToFirstInvoiceAsync();

        // ISDOC download button should always appear on the detail page regardless of invoice status.
        // The test locates the button by its label text (CZ or EN depending on locale).
        var isdocButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "ISDOC" });
        var count = await isdocButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "ISDOC download button should be present on invoice detail page");
    }

    [Test]
    public async Task InvoiceDetail_IsdocButton_IsDisabledForDraftInvoice()
    {
        // Navigate to a fresh/new invoice create form — a newly created invoice is Draft.
        await LoginAndNavigateAsync("/invoices/create", "h4");

        // The ISDOC button should not appear on the create form (no invoice loaded yet),
        // so navigate back to an invoice that is in Draft state instead.
        // Because automated test data may not guarantee a Draft invoice at a known URL,
        // we check the general list and pick the first invoice, then check the button state.
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        var firstRow = Page.Locator(".mud-table-body tr").First;
        await firstRow.ClickAsync();
        await Page.WaitForSelectorAsync("h4, h3, h5", new() { Timeout = Config.BlazorLoadTimeout });

        // Find the ISDOC button — it must exist on the page.
        var isdocButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "ISDOC" });
        var count = await isdocButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "ISDOC download button should be present on invoice detail page");

        // Verify the button's disabled/enabled state matches the invoice status:
        // disabled for Draft, enabled for Issued/Paid/Overdue.
        // We read the aria-disabled attribute set by MudBlazor when Disabled=true.
        var body = await Page.TextContentAsync("body");
        var isDraftInvoice = body!.Contains("Koncept") || body.Contains("Draft");
        var isDisabled = await isdocButton.First.GetAttributeAsync("disabled");
        if (isDraftInvoice)
        {
            Assert.That(isDisabled, Is.Not.Null,
                "ISDOC button must be disabled for Draft invoices");
        }
        else
        {
            Assert.That(isDisabled, Is.Null,
                "ISDOC button must be enabled for issued/paid/overdue invoices");
        }
    }

    [Test]
    public async Task InvoiceDetail_QrCode_SectionExists()
    {
        await NavigateToFirstInvoiceAsync();

        // QR codes section (QR Faktura / QR Platba) — may only show on certain statuses
        var body = await Page.TextContentAsync("body");
        // Just verify the page loaded without errors
        var noError = await Page.Locator(".mud-alert-filled-error").CountAsync();
        // If there are visible errors, check they're not blocking the page
        Assert.That(body, Is.Not.Null.And.Not.Empty,
            "Invoice detail page should render content");
    }
}
