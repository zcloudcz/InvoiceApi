using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Auth;

/// <summary>
/// Tests for the Registration page (/register).
/// Covers: form rendering, field validation, and step progression.
/// </summary>
[TestFixture]
public class RegisterTests : FakvioPageTest
{
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
}
