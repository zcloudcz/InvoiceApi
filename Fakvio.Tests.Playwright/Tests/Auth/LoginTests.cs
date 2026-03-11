using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Auth;

/// <summary>
/// Tests for the login page (/login).
/// Validates the authentication flow: valid credentials, invalid credentials,
/// form elements presence, and registration link.
///
/// These tests interact with the actual login UI (not localStorage injection)
/// to verify the real user authentication experience.
/// </summary>
[TestFixture]
public class LoginTests : FakvioPageTest
{
    [Test]
    public async Task Login_PageLoads_ShowsLoginForm()
    {
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        // Verify email and password fields are visible
        await Expect(Page.GetByLabel("E-mail")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Heslo")).ToBeVisibleAsync();

        // Verify login button is present
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Přihlásit" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Login_ValidCredentials_RedirectsToDashboard()
    {
        await LoginViaUiAsync();

        // Wait for redirect to dashboard — login does forceLoad: true causing full page reload
        // After login, wait for WASM to re-initialize on the new page
        await Page.WaitForURLAsync(url => !url.Contains("/login"), new() { Timeout = Config.BlazorLoadTimeout });

        // Wait for some content to render (page should have rendered something after auth)
        await Page.WaitForSelectorAsync("body", new() { Timeout = Config.BlazorLoadTimeout });

        // Verify URL is no longer /login
        Assert.That(Page.Url, Does.Not.Contain("/login"), "Should have navigated away from login page");
    }

    [Test]
    public async Task Login_InvalidCredentials_ShowsError()
    {
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        // Fill wrong credentials
        await Page.GetByLabel("E-mail").FillAsync("wrong@example.com");
        await Page.GetByLabel("Heslo").FillAsync("WrongPassword123");

        // Submit
        await Page.GetByRole(AriaRole.Button, new() { Name = "Přihlásit" }).ClickAsync();

        // Wait for error alert to appear (API returns 401, Blazor shows MudAlert)
        await Expect(Page.Locator(".mud-alert-filled-error")).ToBeVisibleAsync(
            new() { Timeout = 10_000 });

        // Verify we're still on the login page
        Assert.That(Page.Url, Does.Contain("/login"));
    }

    [Test]
    public async Task Login_OAuthButtons_AreVisible()
    {
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        // Verify OAuth provider buttons (Google, Microsoft, Facebook, Apple)
        await Expect(Page.Locator("button[title='Google']")).ToBeVisibleAsync();
        await Expect(Page.Locator("button[title='Microsoft']")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Login_RegistrationLink_IsVisible()
    {
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        // Verify the registration link exists and points to /register
        var registerLink = Page.Locator("a[href='/register']");
        await Expect(registerLink).ToBeVisibleAsync();
    }
}
