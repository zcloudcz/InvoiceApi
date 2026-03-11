using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Navigation;

/// <summary>
/// Tests for route protection.
/// Verifies that unauthenticated users are redirected to /login when
/// trying to access protected pages.
///
/// Blazor WASM uses [Authorize] attribute on pages. The CustomAuthenticationStateProvider
/// checks localStorage for a valid JWT. Without it, the authorization layer
/// prevents rendering and the UnauthorizedRedirectHandler redirects to /login.
/// </summary>
[TestFixture]
public class AuthorizationTests : FakvioPageTest
{
    [Test]
    [TestCase("/")]
    [TestCase("/invoices")]
    [TestCase("/clients")]
    [TestCase("/received-invoices")]
    [TestCase("/vat-rates")]
    public async Task ProtectedRoute_WithoutAuth_RedirectsToLogin(string path)
    {
        // Navigate to a protected page without logging in
        await Page.GotoAsync(path, new() { WaitUntil = WaitUntilState.NetworkIdle });

        // Wait for Blazor to load and evaluate auth state
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });

        // Should end up on the login page — either via redirect or by showing the login form
        // Blazor WASM may show "Not authorized" or redirect to /login depending on config
        var url = Page.Url;
        var hasLoginForm = await Page.GetByLabel("E-mail").IsVisibleAsync();
        var hasNotAuthorized = await Page.Locator("text=Not authorized").IsVisibleAsync();

        Assert.That(url.Contains("/login") || hasLoginForm || hasNotAuthorized,
            $"Expected redirect to login or 'Not authorized' for {path}, but got URL: {url}");
    }

    [Test]
    public async Task AuthenticatedUser_CanAccessDashboard()
    {
        // Login and verify dashboard is accessible
        await LoginAndNavigateAsync("/");

        // Should see dashboard content, not a login form
        var hasNavMenu = await Page.Locator(".mud-nav-link").CountAsync();
        Assert.That(hasNavMenu, Is.GreaterThan(0), "Expected nav menu to be visible on dashboard");
    }
}
