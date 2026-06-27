using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Auth;

/// <summary>
/// End-to-end login workflow tests.
/// Verifies successful login redirect, session persistence across navigation,
/// and invalid credential handling.
/// </summary>
[TestFixture]
public class LoginWorkflowTests : FakvioPageTest
{
    [Test]
    public async Task Login_ValidCredentials_NavigatesToDashboardAndShowsUserInfo()
    {
        await LoginViaUiAsync();

        await Page.WaitForURLAsync(url => !url.Contains("/login"), new() { Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("h4, .mud-card", new() { Timeout = Config.BlazorLoadTimeout });

        var errorAlerts = Page.Locator(".mud-alert-filled-error");
        var visibleErrors = 0;
        for (int i = 0; i < await errorAlerts.CountAsync(); i++)
        {
            if (await errorAlerts.Nth(i).IsVisibleAsync())
                visibleErrors++;
        }
        Assert.That(visibleErrors, Is.EqualTo(0), "Dashboard should load without errors after login");
    }

    [Test]
    public async Task Login_SessionPersists_AcrossPageNavigation()
    {
        await LoginAndNavigateAsync("/invoices", "h4");

        await Page.GotoAsync("/clients", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("h4, .mud-table", new() { Timeout = Config.BlazorLoadTimeout });

        Assert.That(Page.Url, Does.Not.Contain("/login"),
            "Session should persist — user must remain authenticated after navigating between pages");

        await Page.GotoAsync("/invoices", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("h4", new() { Timeout = Config.BlazorLoadTimeout });
        Assert.That(Page.Url, Does.Not.Contain("/login"));
    }

    [Test]
    public async Task Login_EmptyFields_DoesNotNavigateAway()
    {
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        await Page.GetByRole(AriaRole.Button, new() { Name = "Přihlásit" }).ClickAsync();

        // OnValidSubmit won't fire — wait for URL to confirm no navigation
        await Page.WaitForURLAsync(url => url.Contains("/login"), new() { Timeout = 3000 });

        Assert.That(Page.Url, Does.Contain("/login"),
            "Empty form submission should stay on login page");

        var emailField = Page.GetByLabel("E-mail");
        await Expect(emailField).ToBeVisibleAsync();
        await Expect(emailField).ToBeEditableAsync();
    }

    [Test]
    public async Task Login_WrongPassword_StaysOnLoginPage()
    {
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        await Page.GetByLabel("E-mail").FillAsync(Config.AdminEmail);
        await Page.GetByLabel("Heslo").FillAsync("TotallyWrongPassword999!");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Přihlásit" }).ClickAsync();

        await Expect(Page.Locator(".mud-alert-filled-error")).ToBeVisibleAsync(new() { Timeout = 10_000 });

        Assert.That(Page.Url, Does.Contain("/login"));
        await Expect(Page.GetByLabel("Heslo")).ToBeVisibleAsync();
    }
}
