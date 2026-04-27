using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Settings;

/// <summary>
/// Tests for the My Company page (/my-company).
/// </summary>
[TestFixture]
public class MyCompanyTests : FakvioPageTest
{
    // Localized strings used by the bank-account-mailbox dialog tests.
    // The Playwright context is configured with cs-CZ, so we match the Czech values
    // from SharedResource.resx (section title + add-mode info hint).
    private const string MailboxSectionTitleCz = "Email pro párování plateb";
    private const string MailboxAvailableAfterSaveCz = "Mailbox bude možné nastavit po prvním uložení účtu.";
    private const string AddBankAccountButtonCz = "Přidat účet";
    private const string EditBankAccountTooltipCz = "Upravit účet";

    [SetUp]
    public async Task SetUp()
    {
        await LoginAndNavigateAsync("/my-company", "h4");
    }

    [Test]
    public async Task MyCompany_PageLoads_ShowsHeading()
    {
        var heading = Page.Locator("h4");
        await Expect(heading).ToBeVisibleAsync();
    }

    [Test]
    public async Task MyCompany_HasInputFields()
    {
        var inputs = Page.Locator(".mud-input input");
        var count = await inputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "My Company page should have input fields for company details");
    }

    /// <summary>
    /// Add mode of the bank-account dialog must show:
    /// - the section title (BankMailbox_Section_Title)
    /// - the "available after save" info hint (BankMailbox_AvailableAfterSave)
    /// and must NOT render the BankAccountMailboxCard (which would be useless without
    /// a persisted bank-account Id).
    ///
    /// This test is independent of any seeded bank-account data — it always works
    /// because it goes through the "Add" button path.
    /// </summary>
    [Test]
    public async Task BankAccountDialog_AddMode_ShowsAvailableAfterSaveHint()
    {
        // Enter edit mode so the BankAccountListEditor's "Add" button becomes available.
        await EnterEditModeAsync();

        // Click "Add" on the BankAccountListEditor section
        var addButton = Page.GetByRole(AriaRole.Button, new() { Name = AddBankAccountButtonCz });
        await Expect(addButton).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await addButton.ClickAsync();

        // Wait for dialog to render
        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Section title should be present (rendered for both add and edit modes)
        var sectionTitle = dialog.GetByText(MailboxSectionTitleCz);
        await Expect(sectionTitle).ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Add-mode info hint must be visible
        var hint = dialog.GetByText(MailboxAvailableAfterSaveCz);
        await Expect(hint).ToBeVisibleAsync(new() { Timeout = 5_000 });

        // BankAccountMailboxCard must NOT be rendered (it would talk to /api/payment-matching
        // and bind to a real Id, which is not available before first save).
        // The card always renders the Activate button or the active chip; the safest invariant
        // is that the BankMailbox_Title ("Párování plateb") header from the card is absent.
        var cardHeader = dialog.GetByText("Párování plateb", new() { Exact = true });
        await Expect(cardHeader).ToHaveCountAsync(0);

        // Cancel the dialog so it does not pollute subsequent assertions.
        var cancelBtn = Page.GetByRole(AriaRole.Button, new() { Name = "Zrušit" });
        await cancelBtn.First.ClickAsync();
    }

    /// <summary>
    /// Edit mode of the bank-account dialog must render the BankAccountMailboxCard
    /// (BankMailbox_Section_Title is shown and the add-mode hint is NOT shown).
    ///
    /// Depends on at least one bank account being already saved on the issuer.
    /// If none exists in the test database, the test is inconclusive — better than
    /// failing on a fixture concern.
    /// </summary>
    [Test]
    public async Task BankAccountDialog_EditMode_RendersMailboxCard()
    {
        await EnterEditModeAsync();

        // Look for an existing edit icon button on a bank-account row
        var editButton = Page.GetByRole(AriaRole.Button, new() { Name = EditBankAccountTooltipCz });
        var editCount = await editButton.CountAsync();
        if (editCount == 0)
        {
            Assert.Inconclusive(
                "No existing bank account found on the issuer — cannot exercise the dialog edit mode. " +
                "Seed at least one bank account on the test company to enable this assertion.");
            return;
        }

        await editButton.First.ClickAsync();

        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Section title is always shown
        var sectionTitle = dialog.GetByText(MailboxSectionTitleCz);
        await Expect(sectionTitle).ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Add-mode hint must NOT be visible in edit mode
        var hint = dialog.GetByText(MailboxAvailableAfterSaveCz);
        await Expect(hint).ToHaveCountAsync(0);

        // BankAccountMailboxCard must be rendered — its header "Párování plateb" is the marker.
        var cardHeader = dialog.GetByText("Párování plateb", new() { Exact = true });
        await Expect(cardHeader.First).ToBeVisibleAsync(new() { Timeout = 5_000 });

        // Cleanly close the dialog
        var cancelBtn = Page.GetByRole(AriaRole.Button, new() { Name = "Zrušit" });
        await cancelBtn.First.ClickAsync();
    }

    /// <summary>
    /// Helper: clicks the "Edit" toolbar button on /my-company and waits until the
    /// BankAccountListEditor renders its "Add" button (signals edit mode is fully active).
    /// </summary>
    private async Task EnterEditModeAsync()
    {
        var editToolbar = Page.GetByRole(AriaRole.Button, new() { Name = "Upravit" });
        await Expect(editToolbar).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await editToolbar.ClickAsync();

        // Wait for the BankAccountListEditor to switch to edit mode
        // (Add button becomes visible only when ReadOnly == false)
        await Page.GetByRole(AriaRole.Button, new() { Name = AddBankAccountButtonCz })
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5_000 });
    }
}
