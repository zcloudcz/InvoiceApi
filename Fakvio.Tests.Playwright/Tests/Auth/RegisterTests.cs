using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Auth;

/// <summary>
/// Tests for the Registration page (/register) and for the onboarding journey that starts there.
///
/// Three kinds of test live here:
/// - the cheap rendering checks on /register itself,
/// - one long journey (issue #221, story #150) that walks a brand-new company all the way from
///   the registration form to a dashboard with nothing left blocking invoicing,
/// - the refusal paths of /set-password, the step of that journey that arrives by e-mail and is
///   therefore the one a user can reach with a broken or already-spent link.
/// </summary>
[TestFixture]
public class RegisterTests : FakvioPageTest
{
    /// <summary>
    /// Width of the browser window every test in this fixture runs at.
    ///
    /// The chat drawer (#358) is declared with <c>Breakpoint="Breakpoint.Lg"</c>, and MudBlazor
    /// puts that boundary at exactly 1280 px: at 1280 and above the drawer clips the content and
    /// leaves it clickable, below it turns into an overlay that swallows clicks. Playwright's
    /// default window happens to be exactly 1280 px wide, i.e. exactly on the boundary — and the
    /// onboarding journey is precisely the situation in which the assistant opens itself, because
    /// the setup is not finished yet. The width is therefore pinned well clear of the boundary
    /// instead of being inherited from a library default that may move.
    /// </summary>
    private const int DesktopViewportWidth = 1440;

    /// <summary>Height that goes with <see cref="DesktopViewportWidth"/>; no breakpoint depends on it.</summary>
    private const int DesktopViewportHeight = 900;

    /// <inheritdoc />
    public override BrowserNewContextOptions ContextOptions()
    {
        var options = base.ContextOptions();
        options.ViewportSize = new ViewportSize
        {
            Width = DesktopViewportWidth,
            Height = DesktopViewportHeight
        };
        return options;
    }

    [Test]
    public async Task Register_PageLoads_ShowsForm()
    {
        await Page.GotoAsync("/register", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        // Should have email, password fields
        var inputs = Page.Locator(".mud-input input");
        var count = await inputs.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(2),
            "Registration form should have at least email and password fields");
    }

    [Test]
    public async Task Register_PageLoads_ShowsHeading()
    {
        await Page.GotoAsync("/register", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        var body = await Page.TextContentAsync("body");
        var hasRegister = body!.Contains("Registrace") || body.Contains("Register")
            || body.Contains("Sign up") || body.Contains("Vytvořit účet");
        Assert.That(hasRegister, Is.True,
            "Registration page should have a registration heading");
    }

    [Test]
    public async Task Register_HasSubmitButton()
    {
        await Page.GotoAsync("/register", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        var submitButton = Page.GetByRole(AriaRole.Button).Filter(
            new() { HasText = "Registrovat" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Register" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Vytvořit" })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = "Create" }))));
        var count = await submitButton.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Registration page should have a submit button");
    }

    [Test]
    public async Task Register_HasLoginLink()
    {
        await Page.GotoAsync("/register", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        var loginLink = Page.Locator("a[href='/login']");
        var count = await loginLink.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(1),
            "Registration page should have a link to login");
    }

    [Test]
    public async Task Register_HasOAuthButtons()
    {
        await Page.GotoAsync("/register", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        // OAuth buttons (Google, Microsoft)
        var oauthButtons = Page.Locator("button[title='Google'], button[title='Microsoft']");
        var count = await oauthButtons.CountAsync();
        Assert.That(count, Is.GreaterThanOrEqualTo(0),
            "Registration may show OAuth provider buttons");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Onboarding journey — issue #221
    // ─────────────────────────────────────────────────────────────────────────────

    // Localized strings from SharedResource.resx. The Playwright context runs cs-CZ
    // (see FakvioPageTest.ContextOptions), so the Czech values are the ones rendered.
    private const string RegisterSuccessCz = "Registrace úspěšná!";
    private const string SetPasswordSuccessCz = "Heslo bylo úspěšně nastaveno!";
    private const string BlockingGroupTitleCz = "Než začnete fakturovat, doplňte tato nastavení";
    private const string WarningGroupTitleCz = "Doporučená nastavení k doplnění";
    private const string BankAccountIssueCz = "Vlastní firma nemá bankovní účet";
    private const string FixLinkCz = "Doplnit";
    private const string SaveSuccessCz = "Uloženo úspěšně";
    private const string NewPasswordLabelCz = "Nové heslo";
    private const string TokenMissingCz = "Chybí token pozvánky. Zkontrolujte odkaz v emailu.";
    private const string TokenRejectedCz = "Pozvánka je neplatná nebo vypršela. Požádejte administrátora o novou pozvánku.";

    /// <summary>Password the journey sets for the account it registers (min. 6 chars).</summary>
    private const string NewUserPassword = "E2eHeslo123!";

    /// <summary>
    /// Setting the password hands the tenant provisioner a whole PostgreSQL schema to create
    /// and migrate, and registration waits for an SMTP attempt that has nowhere to deliver.
    /// Both are far slower than a page load, hence a separate, larger budget.
    /// </summary>
    private const int OnboardingStepTimeout = 90_000;

    /// <summary>
    /// The deterministic fallback path of story #150, end to end:
    /// register → set password → first login → finish the setup through the guide →
    /// the checklist no longer blocks invoicing.
    ///
    /// Deliberately AI-free. Story #150 (question 4) settled that E2E covers this
    /// fallback guide, while the conversational onboarding is covered by unit and
    /// integration tests of its context and prompt (task #214).
    ///
    /// "Done" here means <b>nothing blocking left</b>, not an empty report: every fresh
    /// tenant also gets the EPO_HEADER_INCOMPLETE warning, whose fix link points at a
    /// SysAdmin-only page (issue #345), so a tenant admin cannot clear it at all. The
    /// warning group is asserted to survive, which also proves the report was re-read
    /// rather than the whole component having failed to load.
    ///
    /// One run leaves one provisioned tenant behind. There is no API that drops a tenant
    /// schema (DELETE /api/company/{id} is a soft delete), so cleaning up would be
    /// half-done anyway — the suite already writes to the shared dev database.
    /// </summary>
    [Test]
    public async Task Register_SetPassword_FirstLogin_ThenGuide_LeavesNothingBlocking()
    {
        // Registration has two unique columns behind it — User.Email and
        // IX_Client_RegistrationNumber — so one random run id feeds both and a second run
        // cannot collide with the company the first one left behind.
        // ".invalid" is reserved by RFC 2606 and can never be delivered, so repeated runs
        // cannot mail a stranger; registration succeeds either way, the send result only
        // changes the snackbar.
        var runId = Random.Shared.NextInt64(100_000_000, 999_999_999);
        var email = $"e2e-onboarding-{runId}@fakvio-e2e.invalid";

        await SubmitRegistrationFormAsync(email, runId.ToString());

        var sysAdminToken = await AuthHelper.GetTokenAsync(
            Config.ApiUrl, Config.AdminEmail, Config.AdminPassword);
        var invitationToken = await AuthHelper.GetInvitationTokenAsync(
            Config.ApiUrl, sysAdminToken, email);

        await SetPasswordAsync(invitationToken);

        // The invitation is single use — setting the password clears the token server-side.
        // Asserted here rather than in a test of its own because this is the only place in the
        // suite that ever holds a real, freshly issued token; anywhere else it would be a fake.
        await ExpectSetPasswordRefusesLinkAsync(SetPasswordUrl(invitationToken), TokenRejectedCz);

        // First login — through the real form, as the freshly created account.
        await LoginViaUiAsync(email, NewUserPassword);

        // The dashboard greets the new tenant with the guide. Exactly one thing blocks
        // invoicing: registration collects no bank account.
        var blockingGroup = ChecklistGroup(BlockingGroupTitleCz);
        await Expect(blockingGroup).ToBeVisibleAsync(new() { Timeout = OnboardingStepTimeout });
        await Expect(blockingGroup.GetByText(BankAccountIssueCz)).ToBeVisibleAsync();

        // Follow the guide. Scoping the link to the blocking group matters: the warning group
        // carries a "Doplnit" link of its own that leads to a page this user may not open (#345).
        // No .First here on purpose — Playwright's strict mode then also asserts what the two
        // lines above only imply, namely that the bank account is the *only* thing blocking.
        await blockingGroup.GetByRole(AriaRole.Link, new() { Name = FixLinkCz }).ClickAsync();
        await Page.WaitForURLAsync("**/my-company", new() { Timeout = Config.BlazorLoadTimeout });

        await AddBankAccountAsync("2002002002/2010");

        // Back on the dashboard the guide re-reads the report on every visit — it stores no
        // "done" flag, so this is the live answer, not a cached one.
        await Page.GotoAsync("/", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        await Expect(ChecklistGroup(WarningGroupTitleCz))
            .ToBeVisibleAsync(new() { Timeout = OnboardingStepTimeout });
        await Expect(ChecklistGroup(BlockingGroupTitleCz)).ToHaveCountAsync(0);
    }

    /// <summary>
    /// A link with no token at all — a mail client that dropped the query string, or somebody
    /// typing the address by hand. The page must say why instead of offering an empty form.
    /// </summary>
    [Test]
    public async Task SetPassword_WithoutToken_RefusesTheLink()
        => await ExpectSetPasswordRefusesLinkAsync("/set-password", TokenMissingCz);

    /// <summary>
    /// A well-formed token that belongs to nobody. This is the branch the API answers, as opposed
    /// to the missing-token case above, which the page decides on its own without a round trip.
    /// </summary>
    [Test]
    public async Task SetPassword_WithUnknownToken_RefusesTheLink()
        => await ExpectSetPasswordRefusesLinkAsync(SetPasswordUrl(Guid.NewGuid().ToString()), TokenRejectedCz);

    /// <summary>
    /// Opens a "set your password" link that must not work and asserts the page turned it down:
    /// the reason is on screen and the form is not there at all. The second half is the load
    /// bearing one — an error banner sitting above a usable form would still let the caller
    /// through.
    /// </summary>
    private async Task ExpectSetPasswordRefusesLinkAsync(string relativeUrl, string expectedMessage)
    {
        await Page.GotoAsync(relativeUrl, new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        await Expect(Page.GetByText(expectedMessage))
            .ToBeVisibleAsync(new() { Timeout = Config.BlazorLoadTimeout });
        await Expect(Page.GetByLabel(NewPasswordLabelCz)).ToHaveCountAsync(0);
    }

    /// <summary>Builds the invitation link the registration mail would contain.</summary>
    private static string SetPasswordUrl(string token)
        => $"/set-password?token={Uri.EscapeDataString(token)}";

    /// <summary>
    /// One severity block of the setup checklist. <c>setup-checklist-group</c> is the structural
    /// wrapper the component renders per severity (it carries no styling); the heading text is
    /// what tells the blocking block from the warning one.
    /// </summary>
    private ILocator ChecklistGroup(string groupTitle) =>
        Page.Locator(".setup-checklist-group").Filter(new() { HasText = groupTitle });

    /// <summary>
    /// Fills and submits /register for a brand-new company, then waits for the success panel.
    ///
    /// Two choices keep the resulting readiness report the same on every run:
    /// - the address is typed rather than pulled from ARES — a typed address wins over the
    ///   ARES one server-side, so the registered office is always complete;
    /// - <paramref name="registrationNumber"/> is nine digits, one more than a real IČO.
    ///   AresServiceImpl rejects anything that is not exactly eight characters before it
    ///   opens a socket, so the journey never depends on the live registry, and the new
    ///   company stays a non-VAT payer — which takes the DIČ rule out of the picture too.
    ///
    /// What is left is a single blocking item: the missing bank account.
    /// </summary>
    private async Task SubmitRegistrationFormAsync(string email, string registrationNumber)
    {
        await Page.GotoAsync("/register", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        await Page.GetByLabel("E-mail").FillAsync(email);
        await Page.GetByLabel("Jméno").FillAsync("Eva");
        await Page.GetByLabel("Příjmení").FillAsync("Nováková");
        await Page.GetByLabel("Název firmy").FillAsync("E2E Onboarding s.r.o.");
        await Page.GetByLabel("IČO").FillAsync(registrationNumber);
        await Page.GetByLabel("Ulice").FillAsync("Testovací 1");
        await Page.GetByLabel("PSČ").FillAsync("11000");
        await Page.GetByLabel("Město").FillAsync("Praha");
        await Page.GetByLabel("Země").FillAsync("Česká republika");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Zaregistrovat se" }).ClickAsync();

        // The success alert is shown whether or not the mail went out; only the snackbar differs.
        await Expect(Page.GetByText(RegisterSuccessCz).First)
            .ToBeVisibleAsync(new() { Timeout = OnboardingStepTimeout });
    }

    /// <summary>
    /// Opens the emailed link and sets the password. Submitting it also verifies the email and
    /// provisions the tenant schema, so the success alert is the point at which the workspace
    /// exists — the warning variant of the alert would mean provisioning failed.
    /// </summary>
    private async Task SetPasswordAsync(string invitationToken)
    {
        await Page.GotoAsync(SetPasswordUrl(invitationToken), new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        var newPassword = Page.GetByLabel(NewPasswordLabelCz);
        await Expect(newPassword).ToBeVisibleAsync(new() { Timeout = Config.BlazorLoadTimeout });
        await newPassword.FillAsync(NewUserPassword);
        await Page.GetByLabel("Potvrzení hesla").FillAsync(NewUserPassword);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Nastavit heslo" }).ClickAsync();

        await Expect(Page.GetByText(SetPasswordSuccessCz).First)
            .ToBeVisibleAsync(new() { Timeout = OnboardingStepTimeout });
    }

    /// <summary>
    /// Adds the missing bank account on /my-company: edit mode → "Add" dialog → save the page.
    /// The dialog only stages the account in memory, so the page-level save is what persists it.
    /// </summary>
    private async Task AddBankAccountAsync(string accountNumber)
    {
        var editButton = Page.GetByRole(AriaRole.Button, new() { Name = "Upravit" });
        await Expect(editButton).ToBeVisibleAsync(new() { Timeout = Config.BlazorLoadTimeout });
        await editButton.ClickAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Přidat účet" }).ClickAsync();

        var dialog = Page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync(new() { Timeout = Config.BlazorLoadTimeout });
        await dialog.GetByLabel("Číslo účtu").FillAsync(accountNumber);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Uložit" }).ClickAsync();
        await Expect(dialog).ToBeHiddenAsync(new() { Timeout = Config.BlazorLoadTimeout });

        // The page header holds the first "Uložit" in DOM order; later ones belong to the
        // SysAdmin-only SMTP / AI panels, which this tenant admin does not even render.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Uložit" }).First.ClickAsync();
        await WaitForSnackbarAsync(SaveSuccessCz, OnboardingStepTimeout);
    }
}
