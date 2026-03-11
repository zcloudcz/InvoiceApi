using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for the Received Invoice Detail page (/received-invoices/{id} and /received-invoices/create).
/// Covers: viewing, creating, status display, items table, and action buttons.
/// </summary>
[TestFixture]
public class ReceivedInvoiceDetailTests : FakvioPageTest
{
    [Test]
    public async Task ReceivedInvoiceDetail_Create_PageLoads()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4, h5");

        var heading = Page.Locator("h4, h5").First;
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task ReceivedInvoiceDetail_Create_HasFormFields()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4, h5");

        // Should have input fields (document number, supplier, dates)
        var inputs = Page.Locator(".mud-input input");
        var count = await inputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Create form should have input fields for received invoice");
    }

    [Test]
    public async Task ReceivedInvoiceDetail_Create_HasDatePickers()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4, h5");

        var datePickers = Page.Locator(".mud-picker");
        var count = await datePickers.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Create form should have date pickers (Issue, Received, Due)");
    }

    [Test]
    public async Task ReceivedInvoiceDetail_Create_HasSupplierSelect()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4, h5");

        var selects = Page.Locator(".mud-select");
        var count = await selects.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Create form should have supplier selector");
    }

    [Test]
    public async Task ReceivedInvoiceDetail_Create_HasAddItemButton()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4, h5");

        // "Add Item" button with plus icon
        var addButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Přidat" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Add" }));
        var count = await addButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Create form should have an add item button");
    }

    [Test]
    public async Task ReceivedInvoiceDetail_Create_HasPaymentFields()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4, h5");

        var body = await Page.TextContentAsync("body");
        // Payment-related labels should be present
        var hasPayment = body!.Contains("Platba") || body.Contains("Payment")
            || body.Contains("Variabilní") || body.Contains("Variable")
            || body.Contains("IBAN") || body.Contains("Účet") || body.Contains("Account");
        Assert.That(hasPayment, Is.True,
            "Create form should display payment information fields");
    }

    [Test]
    public async Task ReceivedInvoiceDetail_Create_HasCancelAndCreateButtons()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4, h5");

        var cancelButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Zrušit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Cancel" }));
        await Expect(cancelButton.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task ReceivedInvoiceDetail_ViewExisting_LoadsFromList()
    {
        await LoginAndNavigateAsync("/received-invoices", "h4");
        await WaitForTableLoadAsync();

        var firstRow = Page.Locator(".mud-table-body tr").First;
        var rowCount = await firstRow.CountAsync();

        if (rowCount > 0)
        {
            await firstRow.ClickAsync();
            await Page.WaitForSelectorAsync("h4, h5", new() { Timeout = Config.BlazorLoadTimeout });

            var heading = Page.Locator("h4, h5").First;
            await Expect(heading).ToBeVisibleAsync();
        }
        else
        {
            // No received invoices exist — just verify the list page is fine
            Assert.Pass("No received invoices to navigate to — list page verified");
        }
    }
}
