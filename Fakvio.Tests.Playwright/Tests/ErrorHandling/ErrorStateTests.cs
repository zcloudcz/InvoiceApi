using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.ErrorHandling;

/// <summary>
/// Tests for error handling and edge cases.
/// Covers: 404 pages, invalid IDs, and graceful error display.
/// </summary>
[TestFixture]
public class ErrorStateTests : FakvioPageTest
{
    [Test]
    public async Task NotFound_InvalidRoute_ShowsContent()
    {
        await LoginAndNavigateAsync("/", "h4, h3, .mud-card");

        // Navigate to a non-existent route
        await Page.GotoAsync("/this-page-does-not-exist", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        await Task.Delay(2000);

        // Blazor should still render (either 404 component or redirect)
        var body = await Page.TextContentAsync("body");
        Assert.That(body, Is.Not.Null.And.Not.Empty,
            "Non-existent route should render some content (404 or redirect)");
    }

    [Test]
    public async Task InvoiceDetail_InvalidId_HandlesGracefully()
    {
        await LoginAndNavigateAsync("/invoices", "h4");

        // Navigate to a non-existent invoice
        await Page.GotoAsync("/invoices/999999", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        await Task.Delay(3000);

        // Should show error alert or redirect — not a blank page
        var body = await Page.TextContentAsync("body");
        Assert.That(body, Is.Not.Null.And.Not.Empty,
            "Invalid invoice ID should not result in a blank page");
    }

    [Test]
    public async Task ClientDetail_InvalidId_HandlesGracefully()
    {
        await LoginAndNavigateAsync("/clients", "h4");

        await Page.GotoAsync("/clients/999999", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        await Task.Delay(3000);

        var body = await Page.TextContentAsync("body");
        Assert.That(body, Is.Not.Null.And.Not.Empty,
            "Invalid client ID should not result in a blank page");
    }

    [Test]
    public async Task UnauthenticatedAccess_RedirectsToLogin()
    {
        // Navigate without logging in first
        await Page.GotoAsync("/invoices", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        await Task.Delay(3000);

        // Should redirect to login page
        Assert.That(Page.Url, Does.Contain("/login"),
            "Unauthenticated access to protected route should redirect to login");
    }

    [Test]
    public async Task Login_AfterSessionExpiry_RedirectsToLogin()
    {
        await LoginAsAdminAsync();

        // Clear the session to simulate expiry
        await Page.EvaluateAsync("() => localStorage.removeItem('UserSession')");

        // Navigate to a protected page
        await Page.GotoAsync("/invoices", new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        await Task.Delay(3000);

        // Should redirect to login
        Assert.That(Page.Url, Does.Contain("/login"),
            "Expired session should redirect to login");
    }
}
