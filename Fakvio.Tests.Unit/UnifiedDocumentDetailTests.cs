// ============================================================================
// UnifiedDocumentDetailTests - bUnit coverage of the ONE document detail page that serves issued
// documents (/invoices/create, /invoices/{id}) and received invoices (/received-invoices/create,
// /received-invoices/{id}) (issue #542, 2/2).
//
// Every per-type difference comes from DocumentTypeUiPolicy; these tests pin the visible result:
// what the received form looks like, which request a save sends, which buttons each status offers,
// that every transition calls the unified /api/invoice/{id}/... endpoint, that an odd VAT
// percentage survives an edit, and that the one component instance survives navigating between
// the four routes without leaking the previous document.
//
// The stub API answers reference data with a real async delay (see DocumentDetailTestBase), so the
// page renders while OnParametersSetAsync is still awaiting - as in the browser.
// ============================================================================

using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Components.Shared;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using Shouldly;

namespace Fakvio.Tests.Unit;

public class UnifiedDocumentDetailTests : DocumentDetailTestBase
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    public UnifiedDocumentDetailTests()
    {
        Api.ReferenceDelay = TimeSpan.FromMilliseconds(60);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private IRenderedComponent<InvoiceDetail> RenderAt(string path, long id = 0)
    {
        Nav.NavigateTo($"{ApiBaseUrl}{path}");
        return Render<InvoiceDetail>(p => p.Add(c => c.Id, id));
    }

    /// <summary>Waits until the create form is on screen (the item editor is its last piece).</summary>
    private static void WaitForCreateForm(IRenderedComponent<InvoiceDetail> cut) =>
        cut.WaitForAssertion(() => cut.FindComponents<InvoiceItemEditor>().Count.ShouldBe(1), Patience);

    /// <summary>Waits until the status chip of the loaded document is on screen.</summary>
    private static void WaitForDocument(IRenderedComponent<InvoiceDetail> cut) =>
        cut.WaitForAssertion(() => cut.FindAll(".mud-chip").Count.ShouldBeGreaterThan(0), Patience);

    private static string ChipTexts(IRenderedComponent<InvoiceDetail> cut) =>
        string.Join("|", cut.FindAll(".mud-chip").Select(c => c.TextContent.Trim()));

    private string CurrentPath => new Uri(Nav.Uri).AbsolutePath;

    private DetailRequest TheWrite(HttpMethod method, string path) =>
        Api.Writes.Single(w => w.Method == method && w.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the property is missing, null or false - i.e. the request does not carry that issued-only field.</summary>
    private static bool IsAbsent(JsonElement json, string name) =>
        !json.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.False;

    private static readonly string[] ButtonUniverse =
    [
        "Btn_Edit", "ReceivedInvoice_Approve", "Btn_Delete", "Invoice_RevertToDraft", "Invoice_Restore",
        "Invoice_MarkAsPaid", "Invoice_MarkAsUnpaid", "Btn_ExportIsdoc", "Btn_ExportPdf", "Btn_ExportUbl", "Btn_SendEmail"
    ];

    // ── /received-invoices/create ───────────────────────────────────────────

    [Fact]
    public void Received_create_shows_the_received_form_and_none_of_the_issued_only_controls()
    {
        var cut = RenderAt("/received-invoices/create");

        // Still loading: nothing of the form (item editor, type select) may exist before the type is known.
        cut.FindComponents<InvoiceItemEditor>().ShouldBeEmpty();
        cut.FindComponents<MudSelect<EDocumentType>>().ShouldBeEmpty();

        WaitForCreateForm(cut);

        cut.FindComponents<MudSelect<EDocumentType>>().ShouldBeEmpty("the type is fixed by the url");
        cut.FindComponents<BankAccountSelect>().ShouldBeEmpty("the supplier's account is free text");
        cut.FindComponents<InvoiceItemEditor>().Single().Instance.IsReceived.ShouldBeTrue();

        cut.FindComponents<MudSelect<long?>>().ShouldContain(s => s.Instance.Label == L["ReceivedInvoice_Supplier"].Value);
        var markup = cut.Markup;
        markup.ShouldContain(L["ReceivedInvoice_New"].Value);
        markup.ShouldContain("IBAN");
        markup.ShouldContain("SWIFT");
        markup.ShouldNotContain(L["Btn_AddTextRow"].Value);
        markup.ShouldNotContain(L["Oss_ApplyToInvoice"].Value);

        var received = cut.FindComponents<MudDatePicker>()
            .Single(d => d.Instance.Label == L["ReceivedInvoice_ReceivedDate"].Value);
        received.Instance.Date.ShouldBe(DateTime.UtcNow.Date);

        Api.Requests.ShouldNotContain(r => r.Path.Contains("numbersequence", StringComparison.OrdinalIgnoreCase),
            "a received invoice has no number sequence preview");
        Api.Requests.ShouldNotContain(r => r.Path.Contains("oss-country", StringComparison.OrdinalIgnoreCase));
        Api.Requests.ShouldNotContain(r => r.Path.Contains("/client/issuer", StringComparison.OrdinalIgnoreCase),
            "the server picks our own company");
    }

    [Fact]
    public async Task Received_create_posts_the_unified_dto_without_the_issued_only_fields()
    {
        var cut = RenderAt("/received-invoices/create");
        WaitForCreateForm(cut);

        await PickParty(cut, L["ReceivedInvoice_Supplier"].Value, 5);
        await SetText(cut, L["Invoice_DocumentNumber"].Value, "SUP-2026/1");
        // 20 characters, not digits: valid for a supplier's variable symbol (issued ones are digits, max 10).
        await SetText(cut, L["Invoice_VariableSymbol"].Value, "ABC-DEF-GHI-JKL-MNOP");
        await SetText(cut, "IBAN", "CZ6501000000000000000123");
        Click(cut, L["Btn_AddItem"].Value);
        Click(cut, L["Btn_Create"].Value);

        cut.WaitForAssertion(() => Api.Called(HttpMethod.Post, "/api/invoice").ShouldBeTrue(), Patience);

        var body = TheWrite(HttpMethod.Post, "/api/invoice").Json;
        body.GetProperty("documentType").GetInt32().ShouldBe((int)EDocumentType.ReceivedInvoice);
        body.GetProperty("customDocumentNumber").GetString().ShouldBe("SUP-2026/1");
        body.GetProperty("variableSymbol").GetString().ShouldBe("ABC-DEF-GHI-JKL-MNOP");
        body.GetProperty("iban").GetString().ShouldBe("CZ6501000000000000000123");
        body.GetProperty("clientId").GetInt64().ShouldBe(5);
        body.GetProperty("receivedDate").ValueKind.ShouldBe(JsonValueKind.String);
        body.GetProperty("issuerId").GetInt64().ShouldBe(0, "0 = the server uses our own company");
        body.GetProperty("invoiceItem").GetArrayLength().ShouldBe(1);
        body.GetProperty("invoiceItem")[0].GetProperty("isTextRow").GetBoolean().ShouldBeFalse();

        foreach (var issuedOnly in new[] { "numberSequenceId", "bankAccountId", "applyOss", "originalInvoiceId" })
            IsAbsent(body, issuedOnly).ShouldBeTrue($"{issuedOnly} must not be sent for a received invoice");

        // After the save the page moves to the received detail of the new document.
        cut.WaitForAssertion(() => CurrentPath.ShouldBe("/received-invoices/77"), Patience);
    }

    [Fact]
    public async Task Received_create_refuses_a_missing_supplier_and_missing_items_without_a_request()
    {
        var cut = RenderAt("/received-invoices/create");
        WaitForCreateForm(cut);

        Click(cut, L["Btn_Create"].Value);
        cut.WaitForAssertion(() => SnackbarTexts.ShouldContain(L["ReceivedInvoice_SupplierRequired"].Value), Patience);

        await PickParty(cut, L["ReceivedInvoice_Supplier"].Value, 5);
        Click(cut, L["Btn_Create"].Value);
        cut.WaitForAssertion(() => SnackbarTexts.ShouldContain(L["Invoice_AddAtLeastOneItem"].Value), Patience);

        Api.Writes.ShouldBeEmpty("a refused form must not reach the API");
    }

    // ── received view ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(EInvoiceStatus.Draft, "EReceivedInvoiceStatus_Received", "Btn_Edit|ReceivedInvoice_Approve|Btn_Delete|Btn_ExportIsdoc")]
    [InlineData(EInvoiceStatus.Completed, "EReceivedInvoiceStatus_Approved", "Btn_Edit|Invoice_MarkAsPaid|Invoice_RevertToDraft|Btn_ExportIsdoc")]
    [InlineData(EInvoiceStatus.PartiallyPaid, "Invoice_StatusPartiallyPaid", "Btn_Edit|Invoice_MarkAsPaid|Invoice_MarkAsUnpaid|Btn_ExportIsdoc")]
    [InlineData(EInvoiceStatus.Paid, "Invoice_StatusPaid", "Invoice_MarkAsUnpaid|Btn_ExportIsdoc")]
    [InlineData(EInvoiceStatus.Deleted, "Invoice_StatusDeleted", "Invoice_Restore|Btn_ExportIsdoc")]
    public void Received_view_offers_exactly_the_buttons_of_the_status(
        EInvoiceStatus status, string chipKey, string expectedButtonKeys)
    {
        Api.Documents[42] = Received(42, status);
        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        ChipTexts(cut).ShouldBe(L[chipKey].Value);

        var texts = ButtonTexts(cut);
        var present = ButtonUniverse.Where(k => texts.Contains(L[k].Value)).ToList();
        present.ShouldBe(expectedButtonKeys.Split('|'), ignoreOrder: true);

        // Issued-only parts are absent.
        cut.FindComponents<Stub<ReadinessBanner>>().ShouldBeEmpty();
        cut.Markup.ShouldNotContain("QR");
        Api.Requests.ShouldNotContain(r => r.Path.EndsWith("/qr", StringComparison.OrdinalIgnoreCase));
        Api.Requests.ShouldNotContain(r => r.Path.Contains("content-template", StringComparison.OrdinalIgnoreCase));
        Api.Requests.ShouldNotContain(r => r.Path.Contains("/alert", StringComparison.OrdinalIgnoreCase));

        // Attachments are kept under the historical entity name.
        cut.WaitForAssertion(() => Api.Called(HttpMethod.Get, "/api/file-attachment/ReceivedInvoice/42").ShouldBeTrue(), Patience);

        // The payments panel is for Completed, PartiallyPaid and Paid.
        var showsPayments = status is EInvoiceStatus.Completed or EInvoiceStatus.PartiallyPaid or EInvoiceStatus.Paid;
        cut.FindComponents<InvoicePaymentsPanel>().Count.ShouldBe(showsPayments ? 1 : 0);
    }

    [Fact]
    public void Received_view_shows_the_supplier_fields_and_the_vat_regime()
    {
        var doc = Received(42, EInvoiceStatus.Completed);
        doc.InvoiceItem[0].VatRegime = EVatRegime.ReverseCharge;
        Api.Documents[42] = doc;

        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        var markup = cut.Markup;
        markup.ShouldContain(L["ReceivedInvoice_Supplier"].Value);
        markup.ShouldContain("VS-ABC-123");
        markup.ShouldContain("123/0100");
        markup.ShouldContain("KOMBCZPP");
        markup.ShouldContain(L["ReceivedInvoice_ReceivedDate"].Value);
        markup.ShouldContain(L["EVatRegime_ReverseCharge"].Value);
        markup.ShouldContain("15%");
        markup.ShouldNotContain(L["Invoice_Issuer"].Value);
    }

    [Fact]
    public void Received_isdoc_calls_the_received_endpoint_and_names_the_file_after_the_supplier_number()
    {
        Api.Documents[42] = Received(42, EInvoiceStatus.Draft, number: "SUP/9");
        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        Click(cut, L["Btn_ExportIsdoc"].Value);

        cut.WaitForAssertion(() => Api.Called(HttpMethod.Get, "/api/received-invoice/42/isdoc").ShouldBeTrue(), Patience);
        Api.Requests.ShouldNotContain(r => r.Path.Equals("/api/invoice/42/isdoc", StringComparison.OrdinalIgnoreCase));
        cut.WaitForAssertion(() =>
            JSInterop.Invocations["downloadFile"].Single().Arguments[0].ShouldBe("SUP/9.isdoc"), Patience);
    }

    // ── received transitions ────────────────────────────────────────────────

    [Theory]
    [InlineData(EInvoiceStatus.Draft, "ReceivedInvoice_Approve", "/api/invoice/42/complete", "EReceivedInvoiceStatus_Approved")]
    [InlineData(EInvoiceStatus.Completed, "Invoice_MarkAsPaid", "/api/invoice/42/mark-paid", "Invoice_StatusPaid")]
    [InlineData(EInvoiceStatus.Paid, "Invoice_MarkAsUnpaid", "/api/invoice/42/mark-unpaid", "EReceivedInvoiceStatus_Approved")]
    [InlineData(EInvoiceStatus.Completed, "Invoice_RevertToDraft", "/api/invoice/42/revert-to-draft", "EReceivedInvoiceStatus_Received")]
    [InlineData(EInvoiceStatus.Deleted, "Invoice_Restore", "/api/invoice/42/restore", "EReceivedInvoiceStatus_Received")]
    public void Received_transition_buttons_call_the_unified_endpoint_and_the_page_follows(
        EInvoiceStatus from, string buttonKey, string expectedPath, string newChipKey)
    {
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true); // revert to draft asks via window.confirm
        Api.Documents[42] = Received(42, from);
        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        Click(cut, L[buttonKey].Value);

        cut.WaitForAssertion(() =>
        {
            Api.Called(HttpMethod.Post, expectedPath).ShouldBeTrue();
            ChipTexts(cut).ShouldBe(L[newChipKey].Value);
        }, Patience);
        Api.Requests.ShouldNotContain(r => r.Path.Contains("/api/received-invoice/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Received_mark_unpaid_does_nothing_when_the_confirmation_is_declined()
    {
        ConfirmAnswer = false;
        Api.Documents[42] = Received(42, EInvoiceStatus.Paid);
        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        Click(cut, L["Invoice_MarkAsUnpaid"].Value);

        Api.Writes.ShouldBeEmpty();
        ChipTexts(cut).ShouldBe(L["Invoice_StatusPaid"].Value);
    }

    [Fact]
    public void Received_delete_calls_the_unified_endpoint_and_returns_to_the_received_list()
    {
        Api.Documents[42] = Received(42, EInvoiceStatus.Draft);
        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        Click(cut, L["Btn_Delete"].Value);

        cut.WaitForAssertion(() =>
        {
            Api.Called(HttpMethod.Delete, "/api/invoice/42").ShouldBeTrue();
            CurrentPath.ShouldBe("/received-invoices");
        }, Patience);
    }

    // ── received edit ───────────────────────────────────────────────────────

    [Fact]
    public async Task Editing_a_completed_received_invoice_puts_the_number_and_keeps_an_odd_vat_percentage()
    {
        // 15 % is not in the VAT rate list of the tenant (VatRateId == null): the item editor must show it and must
        // not turn it into another rate or 0 when the row is not touched.
        Api.Documents[42] = Received(42, EInvoiceStatus.Completed);
        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        Click(cut, L["Btn_Edit"].Value);
        cut.WaitForAssertion(() => cut.FindComponents<InvoiceItemEditor>().Count.ShouldBe(1), Patience);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("15%"), Patience); // the select shows the stored percentage

        await SetText(cut, L["Invoice_DocumentNumber"].Value, "REC-1-FIXED");
        Click(cut, L["Btn_Save"].Value);

        cut.WaitForAssertion(() => Api.Called(HttpMethod.Put, "/api/invoice/42").ShouldBeTrue(), Patience);
        var body = TheWrite(HttpMethod.Put, "/api/invoice/42").Json;

        body.GetProperty("documentNumber").GetString().ShouldBe("REC-1-FIXED");
        body.GetProperty("iban").GetString().ShouldBe("CZ6501000000000000000123");
        body.GetProperty("swift").GetString().ShouldBe("KOMBCZPP");
        body.GetProperty("receivedDate").ValueKind.ShouldBe(JsonValueKind.String);
        IsAbsent(body, "applyOss").ShouldBeTrue("the server refuses ApplyOss on a received invoice");
        IsAbsent(body, "bankAccountId").ShouldBeTrue();
        IsAbsent(body, "exchangeRate").ShouldBeTrue("the rate is fixed once the invoice is approved");

        var items = body.GetProperty("invoiceItem");
        items.GetArrayLength().ShouldBe(1);
        var item = items[0];
        IsAbsent(item, "vatRateId").ShouldBeTrue("the row had no rate id and must not get one by accident");
        item.GetProperty("vatRatePercentage").GetDecimal().ShouldBe(15m);
        item.GetProperty("vatRegime").GetInt32().ShouldBe((int)EVatRegime.Standard);
        item.GetProperty("description").GetString().ShouldBe("Odd rate item");
        item.GetProperty("unitPrice").GetDecimal().ShouldBe(1000m);
        item.GetProperty("isTextRow").GetBoolean().ShouldBeFalse();
    }


    /// <summary>
    /// The shape of the real (imported) received invoices: one item with VatRateId == null and 21 %, OrderIndex 0,
    /// no due date / variable symbol / payment method / bank fields, and a received date that carries a time of day.
    /// Opening and saving it without touching anything must neither fail client validation nor change a date.
    /// </summary>
    [Fact]
    public void Editing_an_imported_received_invoice_saves_it_back_unchanged()
    {
        var receivedAt = new DateTime(2026, 9, 2, 14, 30, 15, DateTimeKind.Utc);
        var issuedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var doc = Received(42, EInvoiceStatus.Completed, number: "FA2026001");
        doc.IssueDate = issuedAt;
        doc.ReceivedDate = receivedAt;
        doc.TaxableSupplyDate = issuedAt;
        doc.DueDate = null;
        doc.VariableSymbol = null;
        doc.PaymentMethod = null;
        doc.BankAccountNumber = null;
        doc.IBAN = null;
        doc.SWIFT = null;
        doc.InvoiceItem =
        [
            new InvoiceItemDto
            {
                OrderIndex = 0, Description = "Faktura FA2026001", Quantity = 1, Unit = "ks", UnitPrice = 150m,
                VatRateId = null, VatRatePercentage = 21m, VatRegime = EVatRegime.Standard,
                TotalBeforeVat = 150m, VatAmount = 31.5m, TotalWithVat = 181.5m
            },
            new InvoiceItemDto
            {
                OrderIndex = 1, Description = "Odd rate", Quantity = 2, Unit = "ks", UnitPrice = 100m,
                VatRateId = null, VatRatePercentage = 15m, VatRegime = EVatRegime.Standard,
                TotalBeforeVat = 200m, VatAmount = 30m, TotalWithVat = 230m
            }
        ];
        Api.Documents[42] = doc;
        var cut = RenderAt("/received-invoices/42", 42);
        WaitForDocument(cut);

        Click(cut, L["Btn_Edit"].Value);
        cut.WaitForAssertion(() => cut.FindComponents<InvoiceItemEditor>().Count.ShouldBe(1), Patience);
        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("21%");
            cut.Markup.ShouldContain("15%");
        }, Patience);
        Click(cut, L["Btn_Save"].Value);

        cut.WaitForAssertion(() => Api.Called(HttpMethod.Put, "/api/invoice/42").ShouldBeTrue(), Patience);
        SnackbarTexts.ShouldNotContain(s => s.StartsWith(L["Msg_Error"].Value), "no client validation may block the save");
        var body = TheWrite(HttpMethod.Put, "/api/invoice/42").Json;

        body.GetProperty("receivedDate").GetDateTime().ToUniversalTime().ShouldBe(receivedAt, "the time of day must survive");
        body.GetProperty("issueDate").GetDateTime().ToUniversalTime().ShouldBe(issuedAt);
        body.GetProperty("taxableSupplyDate").GetDateTime().ToUniversalTime().ShouldBe(issuedAt);
        IsAbsent(body, "dueDate").ShouldBeTrue();

        var items = body.GetProperty("invoiceItem");
        items.GetArrayLength().ShouldBe(2);
        foreach (var (item, pct, qty, price) in new[] { (items[0], 21m, 1m, 150m), (items[1], 15m, 2m, 100m) })
        {
            IsAbsent(item, "vatRateId").ShouldBeTrue();
            item.GetProperty("vatRatePercentage").GetDecimal().ShouldBe(pct);
            item.GetProperty("quantity").GetDecimal().ShouldBe(qty);
            item.GetProperty("unitPrice").GetDecimal().ShouldBe(price);
            item.GetProperty("vatRegime").GetInt32().ShouldBe((int)EVatRegime.Standard);
            item.GetProperty("isTextRow").GetBoolean().ShouldBeFalse();
        }
        items[0].GetProperty("orderIndex").GetInt32().ShouldBe(0);
    }

    // ── redirects ───────────────────────────────────────────────────────────

    [Fact]
    public void A_received_document_opened_on_the_issued_url_moves_to_the_received_url_and_renders_there()
    {
        Api.Documents[42] = Received(42, EInvoiceStatus.Draft);
        var cut = RenderAt("/invoices/42", 42);

        cut.WaitForAssertion(() => CurrentPath.ShouldBe("/received-invoices/42"), Patience);

        // The same instance stays, the Id is unchanged and a real router may not set the parameters again: the
        // page must notice the new location itself and load the document again on the right side.
        cut.WaitForAssertion(() => ChipTexts(cut).ShouldBe(L["EReceivedInvoiceStatus_Received"].Value), Patience);
        CurrentPath.ShouldBe("/received-invoices/42", "no redirect loop");
    }

    [Fact]
    public void An_issued_document_opened_on_the_received_url_moves_to_the_issued_url_and_renders_there()
    {
        Api.Documents[42] = Issued(42, EInvoiceStatus.Completed);
        var cut = RenderAt("/received-invoices/42", 42);

        cut.WaitForAssertion(() => CurrentPath.ShouldBe("/invoices/42"), Patience);

        cut.WaitForAssertion(() => ChipTexts(cut).ShouldBe(L["Invoice_StatusIssued"].Value), Patience);
        CurrentPath.ShouldBe("/invoices/42");
        cut.Markup.ShouldContain(L["Btn_ExportPdf"].Value);
    }

    // ── one instance, four routes ───────────────────────────────────────────

    [Fact]
    public async Task Navigating_from_the_issued_to_the_received_create_page_does_not_leak_the_issued_form()
    {
        var cut = RenderAt("/invoices/create");
        WaitForCreateForm(cut);

        // Dirty the issued form: a credit note (original-invoice picker) with a note typed in.
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<EDocumentType>>().Instance
            .ValueChanged.InvokeAsync(EDocumentType.CreditNote));
        await cut.InvokeAsync(() => cut.FindComponents<MudTextField<string>>()
            .First(f => f.Instance.Label == L["Invoice_Notes"].Value).Instance.ValueChanged.InvokeAsync("issued note"));
        cut.FindComponents<MudAutocomplete<InvoiceDto>>().ShouldNotBeEmpty();

        // The router reuses the same component instance; Id stays 0, only the path changes (and a real router may
        // not set any parameter again), so the page has to react to the location change itself.
        Nav.NavigateTo($"{ApiBaseUrl}/received-invoices/create");
        // The old form is on screen until the page has reacted: wait for the NEW form (no type select), not just for "a form".
        cut.WaitForAssertion(() =>
        {
            cut.FindComponents<MudSelect<EDocumentType>>().ShouldBeEmpty();
            cut.FindComponents<InvoiceItemEditor>().Count.ShouldBe(1);
        }, Patience);

        cut.FindComponents<MudAutocomplete<InvoiceDto>>().ShouldBeEmpty();
        cut.FindComponents<MudTextField<string>>()
            .First(f => f.Instance.Label == L["Invoice_Notes"].Value).Instance.Value.ShouldBeNullOrEmpty();
        cut.Markup.ShouldContain(L["ReceivedInvoice_New"].Value);

        // The save sends a received document, not the credit note that was being filled in.
        await PickParty(cut, L["ReceivedInvoice_Supplier"].Value, 5);
        Click(cut, L["Btn_AddItem"].Value);
        Click(cut, L["Btn_Create"].Value);
        cut.WaitForAssertion(() => Api.Called(HttpMethod.Post, "/api/invoice").ShouldBeTrue(), Patience);
        var body = TheWrite(HttpMethod.Post, "/api/invoice").Json;
        body.GetProperty("documentType").GetInt32().ShouldBe((int)EDocumentType.ReceivedInvoice);
        IsAbsent(body, "originalInvoiceId").ShouldBeTrue();
        body.TryGetProperty("notes", out var notes).ShouldBeTrue();
        notes.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Navigating_from_the_received_to_the_issued_create_page_brings_the_issued_form_back()
    {
        var cut = RenderAt("/received-invoices/create");
        WaitForCreateForm(cut);

        Nav.NavigateTo($"{ApiBaseUrl}/invoices/create");
        cut.WaitForAssertion(() =>
        {
            cut.FindComponents<MudSelect<EDocumentType>>().Count.ShouldBe(1);
            cut.FindComponents<InvoiceItemEditor>().Count.ShouldBe(1);
        }, Patience);

        cut.FindComponents<BankAccountSelect>().Count.ShouldBe(1);
        cut.FindComponents<InvoiceItemEditor>().Single().Instance.IsReceived.ShouldBeFalse();
        cut.Markup.ShouldNotContain("SWIFT");
    }

    // ── issued behaviour stays ──────────────────────────────────────────────

    [Theory]
    [InlineData(EDocumentType.Invoice, EInvoiceStatus.Completed, true)]
    [InlineData(EDocumentType.CreditNote, EInvoiceStatus.Completed, true)]
    [InlineData(EDocumentType.TaxReceiptForAdvance, EInvoiceStatus.Completed, true)]
    [InlineData(EDocumentType.Proforma, EInvoiceStatus.Completed, false)]
    [InlineData(EDocumentType.Invoice, EInvoiceStatus.Draft, false)]
    public void Issued_view_keeps_pdf_ubl_email_and_the_ubl_rule(EDocumentType type, EInvoiceStatus status, bool ublEnabled)
    {
        Api.Documents[10] = Issued(10, status, type);
        var cut = RenderAt("/invoices/10", 10);
        WaitForDocument(cut);

        var texts = ButtonTexts(cut);
        texts.ShouldContain(L["Btn_ExportPdf"].Value);
        texts.ShouldContain(L["Btn_SendEmail"].Value);
        texts.ShouldContain(L["Btn_ExportIsdoc"].Value);
        Button(cut, L["Btn_ExportUbl"].Value).HasAttribute("disabled").ShouldBe(!ublEnabled);
        Button(cut, L["Btn_ExportIsdoc"].Value).HasAttribute("disabled").ShouldBe(status == EInvoiceStatus.Draft);
        // Draft: readiness banner; otherwise the QR card is requested.
        cut.FindComponents<Stub<ReadinessBanner>>().Count.ShouldBe(status == EInvoiceStatus.Draft ? 1 : 0);
        cut.FindComponents<InvoicePaymentsPanel>().Count.ShouldBe(status == EInvoiceStatus.Completed ? 1 : 0);
    }

    [Fact]
    public void Issued_isdoc_keeps_using_the_unified_endpoint()
    {
        Api.Documents[10] = Issued(10, EInvoiceStatus.Completed);
        var cut = RenderAt("/invoices/10", 10);
        WaitForDocument(cut);

        Click(cut, L["Btn_ExportIsdoc"].Value);

        cut.WaitForAssertion(() => Api.Called(HttpMethod.Get, "/api/invoice/10/isdoc").ShouldBeTrue(), Patience);
        Api.Requests.ShouldNotContain(r => r.Path.Contains("/api/received-invoice/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Issued_partially_paid_has_a_status_label_instead_of_the_raw_enum_name()
    {
        Api.Documents[10] = Issued(10, EInvoiceStatus.PartiallyPaid);
        var cut = RenderAt("/invoices/10", 10);
        WaitForDocument(cut);

        ChipTexts(cut).ShouldBe(L["Invoice_StatusPartiallyPaid"].Value);
    }

    [Fact]
    public void Deleting_an_issued_document_returns_to_the_list_of_its_type()
    {
        Api.Documents[10] = Issued(10, EInvoiceStatus.Draft, EDocumentType.CreditNote);
        var cut = RenderAt("/invoices/10", 10);
        WaitForDocument(cut);

        Click(cut, L["Btn_Delete"].Value);

        cut.WaitForAssertion(() => new Uri(Nav.Uri).PathAndQuery.ShouldBe("/invoices?type=CreditNote"), Patience);
    }
}
