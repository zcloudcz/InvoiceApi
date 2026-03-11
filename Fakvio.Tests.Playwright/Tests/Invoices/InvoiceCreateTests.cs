using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests for the invoice creation page (/invoices/create).
/// Verifies the form renders correctly with all required fields.
/// </summary>
[TestFixture]
public class InvoiceCreateTests : FakvioPageTest
{
    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/invoices/create", "h4");
    }

    [Test]
    public async Task InvoiceCreate_PageLoads_ShowsForm()
    {
        // Client selector (MudSelect)
        var selects = Page.Locator(".mud-select");
        var selectCount = await selects.CountAsync();
        Assert.That(selectCount, Is.GreaterThanOrEqualTo(1),
            "Create form should have at least 1 select dropdown (client)");
    }

    [Test]
    public async Task InvoiceCreate_HasButtons()
    {
        var buttons = Page.GetByRole(AriaRole.Button);
        var count = await buttons.CountAsync();
        Assert.That(count, Is.GreaterThan(0), "Create page should have action buttons");
    }

    [Test]
    public async Task InvoiceCreate_HasDatePickers()
    {
        var datePickers = Page.Locator(".mud-picker");
        var dateCount = await datePickers.CountAsync();
        Assert.That(dateCount, Is.GreaterThanOrEqualTo(1),
            "Create form should have at least 1 date picker");
    }

    [Test]
    public async Task InvoiceCreate_CreditNoteMode_LoadsWithoutError()
    {
        await Page.GotoAsync("/invoices/create?type=CreditNote", new() { WaitUntil = WaitUntilState.NetworkIdle });
        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });

        // No error alert should be visible
        var errorAlert = Page.Locator(".mud-alert-filled-error");
        var errorCount = await errorAlert.CountAsync();
        // Check if any error alerts are visible
        var hasVisibleError = false;
        for (int i = 0; i < errorCount; i++)
        {
            if (await errorAlert.Nth(i).IsVisibleAsync())
            {
                hasVisibleError = true;
                break;
            }
        }
        Assert.That(hasVisibleError, Is.False, "Credit note create page should not show errors");
    }
}
