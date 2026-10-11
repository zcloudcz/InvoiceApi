using System.Text.RegularExpressions;
using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// The whole life of a received invoice on the unified detail page, driven through the UI (issue #542):
/// create -> approve -> mark paid -> mark unpaid (confirm) -> revert to draft -> delete -> restore.
///
/// One test on purpose: every step needs the result of the previous one. Each step waits for the new status chip
/// (auto-waiting Expect), so a button or state that never appears fails the test instead of passing silently.
/// </summary>
[TestFixture]
public class ReceivedInvoiceWorkflowTests : FakvioPageTest
{
    private ILocator Button(string name) => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    private ILocator SupplierSelect => Page.Locator(".mud-main-content .mud-select").Filter(new() { HasText = "Dodavatel" }).First;

    private ILocator StatusChip => Page.Locator(".mud-main-content .mud-chip").First;

    [Test]
    public async Task ReceivedInvoice_FullLifecycle_CreateApprovePayUnpayRevertDeleteRestore()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var supplierName = $"E2E Workflow Supplier {suffix}";
        var number = $"E2E-WF-{suffix}";
        await ApiSeed.CreateClientAsync(Config, supplierName);

        // ── create ──────────────────────────────────────────────────────────
        await LoginAndNavigateAsync("/received-invoices/create", "h4");
        await Expect(SupplierSelect).ToBeVisibleAsync();

        await SupplierSelect.ClickAsync();
        await Page.Locator(".mud-popover-open").GetByText(supplierName).ClickAsync();
        await Expect(Page.Locator(".mud-popover-open")).ToHaveCountAsync(0);

        await Page.GetByLabel("Číslo dokladu").FillAsync(number);
        await Page.GetByLabel("Variabilní symbol").FillAsync("SUP-" + suffix + "-VS"); // free text, not digits

        await Button("Přidat položku").ClickAsync();
        var row = Page.Locator(".mud-main-content .mud-table tbody tr").First;
        await Expect(row).ToBeVisibleAsync();
        var inputs = row.Locator("input");
        await inputs.Nth(2).FillAsync("E2E service");       // description
        await inputs.Nth(5).FillAsync("1000");              // unit price
        await inputs.Nth(5).PressAsync("Tab");

        await Button("Vytvořit").ClickAsync();
        await Page.WaitForURLAsync(new Regex(@"/received-invoices/[0-9]+$"), new() { Timeout = Config.BlazorLoadTimeout });
        var detailUrl = Page.Url;
        await Expect(StatusChip).ToHaveTextAsync("Přijato");
        await Expect(Page.Locator("h4")).ToContainTextAsync(number);

        // ── approve ─────────────────────────────────────────────────────────
        await Button("Schválit").ClickAsync();
        await Expect(StatusChip).ToHaveTextAsync("Schváleno");
        await Expect(Button("Označit jako zaplaceno")).ToBeVisibleAsync();
        await Expect(Button("Smazat")).ToHaveCountAsync(0); // a received invoice is deleted from the draft only

        // ── mark paid ───────────────────────────────────────────────────────
        await Button("Označit jako zaplaceno").ClickAsync();
        await Expect(StatusChip).ToHaveTextAsync("Zaplaceno");
        await Expect(Page.GetByText("Platby").First).ToBeVisibleAsync(); // the payments panel

        // ── mark unpaid, with the confirmation ──────────────────────────────
        await Button("Označit jako nezaplaceno").ClickAsync();
        await Page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Potvrdit", Exact = true }).ClickAsync();
        await Expect(StatusChip).ToHaveTextAsync("Schváleno");

        // ── revert to draft (window.confirm) ────────────────────────────────
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await Button("Vrátit do konceptu").ClickAsync();
        await Expect(StatusChip).ToHaveTextAsync("Přijato");

        // ── delete ──────────────────────────────────────────────────────────
        await Button("Smazat").ClickAsync();
        await Page.WaitForURLAsync(new Regex(@"/received-invoices$"), new() { Timeout = Config.BlazorLoadTimeout });

        // ── it is a deleted document now, and can be restored ───────────────
        await Page.GotoAsync(detailUrl, new() { WaitUntil = WaitUntilState.NetworkIdle, Timeout = Config.BlazorLoadTimeout });
        await Expect(StatusChip).ToHaveTextAsync("Smazáno");
        await Expect(Button("Obnovit")).ToBeVisibleAsync();

        await Button("Obnovit").ClickAsync();
        await Expect(StatusChip).ToHaveTextAsync("Přijato");
    }
}
