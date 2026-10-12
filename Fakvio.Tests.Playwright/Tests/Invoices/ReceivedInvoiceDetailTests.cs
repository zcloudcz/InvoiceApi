using System.Text.RegularExpressions;
using Fakvio.Tests.Playwright.Infrastructure;
using Microsoft.Playwright;
using NUnit.Framework;

namespace Fakvio.Tests.Playwright.Tests.Invoices;

/// <summary>
/// Tests of the ONE document detail page as it serves a RECEIVED invoice
/// (/received-invoices/create and /received-invoices/{id}); issue #542.
///
/// Every assertion waits for the element (Expect / WaitFor) and fails when it is missing: a locator that matches
/// nothing must never make a test pass. Texts are the Czech resources, because the browser context runs in cs-CZ.
/// </summary>
[TestFixture]
public class ReceivedInvoiceDetailTests : FakvioPageTest
{
    private static readonly string Suffix = Guid.NewGuid().ToString("N")[..8];
    private string _supplierName = "";
    private long _supplierId;
    private long _draftId;
    private string _draftNumber = "";

    private ILocator Button(string name) => Page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    /// <summary>The supplier select (a MudSelect is two nested ".mud-select" elements and its input is hidden, so it is found by its caption).</summary>
    private ILocator SupplierSelect => Page.Locator(".mud-main-content .mud-select").Filter(new() { HasText = "Dodavatel" }).First;

    private ILocator StatusChip => Page.Locator(".mud-main-content .mud-chip").First;

    /// <summary>A supplier and one received DRAFT, both created through the API so the tests need nothing pre-existing.</summary>
    [OneTimeSetUp]
    public async Task SeedSupplierAndDraft()
    {
        _supplierName = $"E2E Supplier {Suffix}";
        _supplierId = await ApiSeed.CreateClientAsync(Config, _supplierName);
        _draftNumber = $"E2E-R-{Suffix}";
        _draftId = await ApiSeed.CreateReceivedInvoiceAsync(Config, _supplierId, _draftNumber, vatPercentage: 15m);
    }

    [Test]
    public async Task Create_ShowsTheReceivedForm()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4");

        await Expect(Page.Locator("h4")).ToHaveTextAsync("Nová přijatá faktura");
        await Expect(SupplierSelect).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Datum přijetí")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("Číslo dokladu")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("IBAN")).ToBeVisibleAsync();
        await Expect(Page.GetByLabel("SWIFT")).ToBeVisibleAsync();
        await Expect(Button("Vytvořit")).ToBeVisibleAsync();
        await Expect(Button("Zrušit")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Create_HasThreeDatePickersAndNoIssuedOnlyControls()
    {
        await LoginAndNavigateAsync("/received-invoices/create", "h4");
        await Expect(SupplierSelect).ToBeVisibleAsync();

        // Issue date, received date, due date and DUZP.
        await Expect(Page.Locator(".mud-main-content .mud-picker")).Not.ToHaveCountAsync(0);
        Assert.That(await Page.Locator(".mud-main-content .mud-picker").CountAsync(), Is.GreaterThanOrEqualTo(3));

        // The type is fixed by the url: no document type select, no original invoice picker.
        await Expect(Page.GetByLabel("Typ dokumentu")).ToHaveCountAsync(0);
        await Expect(Page.GetByLabel("Původní faktura")).ToHaveCountAsync(0);

        // Items: add a row; text rows do not exist on a received invoice.
        await Button("Přidat položku").ClickAsync();
        await Expect(Page.Locator(".mud-main-content .mud-table tbody tr")).ToHaveCountAsync(1);
        await Expect(Button("Přidat textový řádek")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task View_ShowsTheReceivedDocumentWithItsActionsAndNoIssuedOnlyOnes()
    {
        await LoginAndNavigateAsync($"/received-invoices/{_draftId}", "h4");

        await Expect(StatusChip).ToHaveTextAsync("Přijato");
        await Expect(Page.Locator("h4")).ToContainTextAsync(_draftNumber);
        await Expect(Page.GetByText(_supplierName).First).ToBeVisibleAsync();

        // A draft received invoice: edit, approve, delete, ISDOC.
        foreach (var name in new[] { "Upravit", "Schválit", "Smazat", "Stáhnout ISDOC" })
            await Expect(Button(name)).ToBeVisibleAsync();

        // Issued-only actions are not there.
        foreach (var name in new[] { "Export PDF", "Stáhnout e-fakturu (UBL)", "Odeslat e-mailem" })
            await Expect(Button(name)).ToHaveCountAsync(0);
    }

    [Test]
    public async Task View_ShowsTheOddVatPercentageOfTheItem()
    {
        await LoginAndNavigateAsync($"/received-invoices/{_draftId}", "h4");

        await Expect(StatusChip).ToHaveTextAsync("Přijato");
        await Expect(Page.Locator(".mud-main-content .mud-table tbody tr").First).ToContainTextAsync("15%");
    }

    [Test]
    public async Task ReceivedDocumentOpenedOnTheIssuedUrl_MovesToTheReceivedUrl()
    {
        await LoginAndNavigateAsync($"/invoices/{_draftId}", "h4");

        await Page.WaitForURLAsync(new Regex($"/received-invoices/{_draftId}$"), new() { Timeout = Config.BlazorLoadTimeout });
        await Expect(StatusChip).ToHaveTextAsync("Přijato");
    }

    [Test]
    public async Task List_OpensTheUnifiedDetail()
    {
        await LoginAndNavigateAsync("/received-invoices", "h4");
        await WaitForTableLoadAsync();

        await Page.GetByText(_draftNumber).First.ClickAsync();

        await Page.WaitForURLAsync(new Regex($"/received-invoices/{_draftId}$"), new() { Timeout = Config.BlazorLoadTimeout });
        await Expect(StatusChip).ToHaveTextAsync("Přijato");
    }

    [Test]
    public async Task Isdoc_DownloadsTheFileNamedAfterTheSuppliersNumber()
    {
        await LoginAndNavigateAsync($"/received-invoices/{_draftId}", "h4");
        await Expect(StatusChip).ToHaveTextAsync("Přijato");

        var download = await Page.RunAndWaitForDownloadAsync(async () => await Button("Stáhnout ISDOC").ClickAsync());

        Assert.That(download.SuggestedFilename, Is.EqualTo($"{_draftNumber}.isdoc"));
    }
}
