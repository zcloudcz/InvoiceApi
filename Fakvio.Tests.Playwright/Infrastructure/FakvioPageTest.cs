using Microsoft.Playwright.NUnit;
using Microsoft.Playwright;
using NUnit.Framework;
using System.Text.Json;

namespace Fakvio.Tests.Playwright.Infrastructure;

/// <summary>
/// Base class for all Fakvio Playwright tests.
/// Extends PageTest (NUnit adapter) with:
/// - Pre-configured browser context (Czech locale, base URL, timezone)
/// - Login helper that injects JWT into localStorage
/// - SysAdmin impersonation for tenant-scoped pages
/// - MudBlazor-aware waiting strategies
///
/// IMPORTANT: The SysAdmin (admin@zcloud.cz) has no CompanyId.
/// To access invoicing pages, we must set ImpersonatedCompanyId in localStorage.
/// Without impersonation, the nav menu hides invoicing sections (_showInvoicing=false).
/// </summary>
public class FakvioPageTest : PageTest
{
    protected readonly TestConfiguration Config = new();

    /// <summary>
    /// Configures the browser context with Czech locale and base URL.
    /// </summary>
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        BaseURL = Config.UiUrl,
        Locale = "cs-CZ",
        TimezoneId = "Europe/Prague",
        IgnoreHTTPSErrors = true
    };

    /// <summary>
    /// Logs in via API and injects the JWT session + impersonation into browser localStorage.
    /// </summary>
    protected async Task LoginAsAdminAsync()
    {
        var sessionJson = await AuthHelper.GetSessionJsonAsync(
            Config.ApiUrl, Config.AdminEmail, Config.AdminPassword);

        // Navigate to set localStorage origin
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForTimeoutAsync(2000);

        // Inject auth session (PascalCase JSON matching Blazor format)
        await Page.EvaluateAsync("(session) => localStorage.setItem('UserSession', session)", sessionJson);

        // Check if SysAdmin (Role=2) — needs impersonation for invoicing pages
        using var doc = JsonDocument.Parse(sessionJson);
        var role = doc.RootElement.GetProperty("Role").GetInt32();
        if (role == 2)
        {
            await Page.EvaluateAsync(@"() => {
                localStorage.setItem('ImpersonatedCompanyId', '1');
                localStorage.setItem('ImpersonatedCompanyName', 'Test Company');
            }");
        }
    }

    /// <summary>
    /// Logs in and navigates to the specified page, waiting for Blazor to fully render.
    /// Uses 'h4' as the default wait selector since it's present on all Fakvio pages
    /// (page heading) and avoids matching hidden MudPopover elements.
    /// </summary>
    protected async Task LoginAndNavigateAsync(string path, string waitForSelector = "h4, .mud-table, .mud-card")
    {
        await LoginAsAdminAsync();

        await Page.GotoAsync(path, new()
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = Config.BlazorLoadTimeout
        });

        // Wait for page content to render
        await Page.WaitForSelectorAsync(waitForSelector, new()
        {
            State = WaitForSelectorState.Visible,
            Timeout = Config.BlazorLoadTimeout
        });
    }

    /// <summary>
    /// Performs login through the actual UI (filling email + password + clicking submit).
    /// </summary>
    protected async Task LoginViaUiAsync()
    {
        await Page.GotoAsync("/login", new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Page.WaitForSelectorAsync("text=Fakvio", new() { Timeout = Config.BlazorLoadTimeout });
        await Page.GetByLabel("E-mail").FillAsync(Config.AdminEmail);
        await Page.GetByLabel("Heslo").FillAsync(Config.AdminPassword);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Přihlásit" }).ClickAsync();
    }

    /// <summary>
    /// Waits for a MudBlazor snackbar to appear with the specified text.
    /// </summary>
    protected async Task<ILocator> WaitForSnackbarAsync(string containsText, int timeoutMs = 10_000)
    {
        var snackbar = Page.Locator(".mud-snackbar").Filter(new() { HasText = containsText });
        await snackbar.WaitForAsync(new() { Timeout = timeoutMs });
        return snackbar;
    }

    /// <summary>
    /// Waits for MudTable loading to complete.
    /// </summary>
    protected async Task WaitForTableLoadAsync(int timeoutMs = 15_000)
    {
        await Task.Delay(300);
        try
        {
            await Page.Locator(".mud-table-loading-progress").WaitForAsync(new()
            {
                State = WaitForSelectorState.Hidden,
                Timeout = timeoutMs
            });
        }
        catch (TimeoutException) { }
    }

    /// <summary>
    /// Creates a bilingual button locator that matches either Czech or English text.
    /// Reduces duplication in tests that must work with both locales.
    /// </summary>
    protected ILocator BilingualButton(string czech, string english) =>
        Page.GetByRole(AriaRole.Button).Filter(new() { HasText = czech })
            .Or(Page.GetByRole(AriaRole.Button).Filter(new() { HasText = english }));

    /// <summary>
    /// Finds the first invoice row matching the given status and clicks through to detail.
    /// Returns true if a matching row was found and navigated to.
    /// </summary>
    protected async Task<bool> NavigateToInvoiceByStatusAsync(string statusText, bool matchStatus = true)
    {
        var rows = Page.Locator(".mud-table-body tr");
        var rowCount = await rows.CountAsync();

        for (int i = 0; i < rowCount; i++)
        {
            var rowText = await rows.Nth(i).TextContentAsync() ?? "";
            var containsStatus = rowText.Contains(statusText, StringComparison.OrdinalIgnoreCase);

            if (containsStatus == matchStatus)
            {
                await rows.Nth(i).ClickAsync();
                await Page.WaitForSelectorAsync("h4, h3, h5", new() { Timeout = Config.BlazorLoadTimeout });
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds a column header by text and returns its locator, or null if not found.
    /// </summary>
    protected async Task<ILocator?> FindColumnHeaderAsync(string czech, string english)
    {
        var headers = Page.Locator(".mud-table-head th");
        var headerCount = await headers.CountAsync();

        for (int i = 0; i < headerCount; i++)
        {
            var text = await headers.Nth(i).TextContentAsync() ?? "";
            if (text.Contains(czech, StringComparison.OrdinalIgnoreCase)
                || text.Contains(english, StringComparison.OrdinalIgnoreCase))
            {
                return headers.Nth(i);
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for any MudBlazor snackbar to appear (success or error).
    /// </summary>
    protected async Task<bool> WaitForAnySnackbarAsync(int timeoutMs = 8_000)
    {
        try
        {
            await Page.Locator(".mud-snackbar").First.WaitForAsync(new() { Timeout = timeoutMs });
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
