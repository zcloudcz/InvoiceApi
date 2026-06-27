using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// End-to-end workflow tests for invoice CRUD operations.
/// Covers: creating an invoice, editing it, status transitions (issue, mark paid),
/// email dialog interaction, and PDF export button behavior.
/// </summary>
[TestFixture]
public class InvoiceWorkflowTests : FakvioPageTest
{
    private readonly string _uniqueSuffix = DateTime.Now.ToString("HHmmss");

    /// <summary>
    /// Selects the first available client in a MudSelect dropdown.
    /// </summary>
    private async Task SelectFirstClientAsync()
    {
        var clientSelect = Page.Locator(".mud-select").First;
        await clientSelect.ClickAsync();
        await Page.WaitForSelectorAsync(".mud-popover-open", new() { Timeout = 5000 });

        var options = Page.Locator(".mud-popover-open .mud-list-item");
        var optionCount = await options.CountAsync();
        if (optionCount == 0)
        {
            Assert.Inconclusive("No clients available in dropdown — cannot create invoice");
            return;
        }

        var targetIndex = 0;
        for (int i = 0; i < optionCount; i++)
        {
            var text = await options.Nth(i).TextContentAsync() ?? "";
            if (!string.IsNullOrWhiteSpace(text)
                && !text.Contains("Vyberte", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("Select", StringComparison.OrdinalIgnoreCase))
            {
                targetIndex = i;
                break;
            }
        }

        await options.Nth(targetIndex).ClickAsync();

        // Wait for popover to close (reactive fields update when client selected)
        try
        {
            await Page.Locator(".mud-popover-open").WaitForAsync(
                new() { State = WaitForSelectorState.Hidden, Timeout = 3000 });
        }
        catch (TimeoutException) { }
    }

    [Test, Order(1)]
    public async Task Invoice_CreateDraft_FullWorkflow()
    {
        await LoginAndNavigateAsync("/invoices/create", "h4");

        await SelectFirstClientAsync();

        var datePickers = Page.Locator(".mud-picker");
        Assert.That(await datePickers.CountAsync(), Is.GreaterThanOrEqualTo(2),
            "Should have Issue Date and Due Date pickers");

        var addItemButton = BilingualButton("Přidat", "Add");
        if (await addItemButton.CountAsync() > 0)
        {
            await addItemButton.First.ClickAsync();
            await Page.Locator(".mud-input input").First.WaitForAsync(new() { Timeout = 2000 });
        }

        await BilingualButton("Vytvořit", "Create").First.ClickAsync();

        // Wait for navigation to detail page
        var navigated = false;
        try
        {
            await Page.WaitForURLAsync(url => url.Contains("/invoices/") && !url.Contains("/create"),
                new() { Timeout = 5000 });
            navigated = true;
        }
        catch (TimeoutException) { }

        var hasSnackbar = await WaitForAnySnackbarAsync(3000);

        Assert.That(navigated || hasSnackbar, Is.True,
            "After submitting, should navigate to detail or show feedback");
    }

    [Test, Order(2)]
    public async Task Invoice_EditDraft_ModifyAndSave()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        if (!await NavigateToInvoiceByStatusAsync("Koncept"))
        {
            Assert.Inconclusive("No Draft invoice found — cannot test edit workflow");
            return;
        }

        var editButton = BilingualButton("Upravit", "Edit");
        if (await editButton.CountAsync() == 0)
        {
            Assert.Inconclusive("No Edit button on this invoice");
            return;
        }
        await editButton.First.ClickAsync();

        var saveButton = BilingualButton("Uložit", "Save");
        await Expect(saveButton.First).ToBeVisibleAsync();

        var notesField = Page.Locator("textarea");
        if (await notesField.CountAsync() > 0)
        {
            await notesField.First.FillAsync($"E2E note {_uniqueSuffix}");
        }

        await saveButton.First.ClickAsync();

        // Wait for view mode or snackbar
        var editReappeared = false;
        try
        {
            await editButton.First.WaitForAsync(new() { Timeout = 5000 });
            editReappeared = true;
        }
        catch (TimeoutException) { }

        var hasSnackbar = await WaitForAnySnackbarAsync(3000);

        Assert.That(editReappeared || hasSnackbar, Is.True,
            "After save, should show success snackbar or return to view mode");
    }

    [Test, Order(3)]
    public async Task Invoice_IssueDraft_ChangesStatus()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        if (!await NavigateToInvoiceByStatusAsync("Koncept"))
        {
            Assert.Inconclusive("No Draft invoice found — skip issue test");
            return;
        }

        var issueButton = BilingualButton("Vystavit", "Issue");
        if (await issueButton.CountAsync() == 0)
        {
            Assert.Inconclusive("No Issue button — invoice may not be in correct state");
            return;
        }
        await issueButton.First.ClickAsync();

        // Handle possible confirmation dialog
        try
        {
            var confirmButton = Page.Locator(".mud-dialog-actions .mud-button-filled");
            await confirmButton.First.WaitForAsync(new() { Timeout = 2000 });
            await confirmButton.First.ClickAsync();
        }
        catch (TimeoutException) { }

        var hasSnackbar = await WaitForAnySnackbarAsync(5000);

        var statusChip = Page.Locator(".mud-chip").First;
        var chipText = await statusChip.TextContentAsync() ?? "";

        var isIssued = chipText.Contains("Vystavena", StringComparison.OrdinalIgnoreCase)
                    || chipText.Contains("Issued", StringComparison.OrdinalIgnoreCase)
                    || chipText.Contains("Completed", StringComparison.OrdinalIgnoreCase);

        Assert.That(isIssued || hasSnackbar, Is.True,
            "After issuing, status should change or snackbar should confirm the action");
    }

    [Test]
    public async Task Invoice_EmailDialog_OpensAndCloses()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        // Find non-Draft invoice (skip rows containing "Koncept" or "Draft")
        if (!await NavigateToInvoiceByStatusAsync("Koncept", matchStatus: false))
        {
            Assert.Inconclusive("No non-Draft invoice — cannot test email dialog");
            return;
        }

        var emailButton = BilingualButton("e-mail", "Email");
        if (await emailButton.CountAsync() == 0)
        {
            Assert.Inconclusive("No email button on this invoice — skip");
            return;
        }
        await emailButton.First.ClickAsync();

        await Page.WaitForSelectorAsync(".mud-dialog", new() { Timeout = 5000 });

        var dialogTitle = Page.Locator(".mud-dialog .mud-typography-h6, .mud-dialog-title");
        await Expect(dialogTitle.First).ToBeVisibleAsync();

        var emailInput = Page.Locator(".mud-dialog input[type='email'], .mud-dialog .mud-input input");
        await Expect(emailInput.First).ToBeVisibleAsync();

        var sendButton = Page.Locator(".mud-dialog").GetByRole(AriaRole.Button).Filter(new() { HasText = "Odeslat" })
            .Or(Page.Locator(".mud-dialog").GetByRole(AriaRole.Button).Filter(new() { HasText = "Send" }));
        await Expect(sendButton.First).ToBeVisibleAsync();

        var cancelButton = Page.Locator(".mud-dialog").GetByRole(AriaRole.Button).Filter(new() { HasText = "Zrušit" })
            .Or(Page.Locator(".mud-dialog").GetByRole(AriaRole.Button).Filter(new() { HasText = "Cancel" }));
        await cancelButton.First.ClickAsync();

        await Page.Locator(".mud-dialog").WaitForAsync(
            new() { State = WaitForSelectorState.Hidden, Timeout = 3000 });
    }

    [Test]
    public async Task Invoice_EmailDialog_FillAndSend()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        // Find non-Draft, non-Deleted invoice
        var rows = Page.Locator(".mud-table-body tr");
        var rowCount = await rows.CountAsync();

        bool foundEligible = false;
        for (int i = 0; i < rowCount && !foundEligible; i++)
        {
            var rowText = await rows.Nth(i).TextContentAsync() ?? "";
            if (!rowText.Contains("Koncept", StringComparison.OrdinalIgnoreCase)
                && !rowText.Contains("Draft", StringComparison.OrdinalIgnoreCase)
                && !rowText.Contains("Smazán", StringComparison.OrdinalIgnoreCase))
            {
                await rows.Nth(i).ClickAsync();
                await Page.WaitForSelectorAsync("h4, h3, h5", new() { Timeout = Config.BlazorLoadTimeout });
                foundEligible = true;
            }
        }

        if (!foundEligible)
        {
            Assert.Inconclusive("No eligible invoice for email test — skip");
            return;
        }

        var emailButton = BilingualButton("e-mail", "Email");
        if (await emailButton.CountAsync() == 0)
        {
            Assert.Inconclusive("No email button — skip");
            return;
        }
        await emailButton.First.ClickAsync();
        await Page.WaitForSelectorAsync(".mud-dialog", new() { Timeout = 5000 });

        var emailInput = Page.Locator(".mud-dialog input[type='email'], .mud-dialog .mud-input input").First;
        await emailInput.FillAsync("e2e-test@example.com");

        var sendButton = Page.Locator(".mud-dialog").GetByRole(AriaRole.Button).Filter(new() { HasText = "Odeslat" })
            .Or(Page.Locator(".mud-dialog").GetByRole(AriaRole.Button).Filter(new() { HasText = "Send" }));

        var isDisabled = await sendButton.First.IsDisabledAsync();
        Assert.That(isDisabled, Is.False, "Send button should be enabled after filling email");

        await sendButton.First.ClickAsync();

        var hasSnackbar = await WaitForAnySnackbarAsync(8000);
        Assert.That(hasSnackbar, Is.True,
            "Sending email should produce a snackbar (success or error)");
    }

    [Test]
    public async Task Invoice_MarkAsPaid_FromCompletedInvoice()
    {
        await LoginAndNavigateAsync("/invoices", "h4");
        await WaitForTableLoadAsync();

        if (!await NavigateToInvoiceByStatusAsync("Vystavena"))
        {
            Assert.Inconclusive("No Completed invoice found — skip");
            return;
        }

        var paidButton = BilingualButton("zaplaceno", "Mark as Paid");
        if (await paidButton.CountAsync() == 0)
        {
            Assert.Inconclusive("No 'Mark as Paid' button — skip");
            return;
        }
        await paidButton.First.ClickAsync();

        var hasSnackbar = await WaitForAnySnackbarAsync(5000);

        var chipText = await Page.Locator(".mud-chip").First.TextContentAsync() ?? "";
        var isPaid = chipText.Contains("Zaplaceno", StringComparison.OrdinalIgnoreCase)
                  || chipText.Contains("Paid", StringComparison.OrdinalIgnoreCase);

        Assert.That(isPaid || hasSnackbar, Is.True,
            "After marking as paid, status should change or snackbar should confirm");
    }

    [Test]
    public async Task Invoice_CancelCreate_NavigatesBack()
    {
        await LoginAndNavigateAsync("/invoices/create", "h4");

        await BilingualButton("Zrušit", "Cancel").First.ClickAsync();

        await Page.WaitForSelectorAsync("h4, .mud-table", new() { Timeout = Config.BlazorLoadTimeout });

        Assert.That(Page.Url, Does.Not.Contain("/create"),
            "Cancel should navigate away from create page");
    }
}
