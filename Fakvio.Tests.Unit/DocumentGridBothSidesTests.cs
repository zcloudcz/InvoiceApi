// ============================================================================
// DocumentGridBothSidesTests - bUnit coverage of the ONE grid that serves issued
// documents (/invoices) and received invoices (/received-invoices) (issue #542).
//
// The page asks DocumentTypeUiPolicy for every per-type difference, so these tests
// pin the visible result: which request is sent, which columns/actions/bulk buttons
// exist per side, and that the same component instance survives navigating between
// the two sides without leaking the previous view (selection, document type).
//
// Expected texts are resolved through the same localizer the page uses, so the
// tests do not depend on the machine's UI culture.
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.User;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Models;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the shared issued/received invoice grid.
/// </summary>
public class DocumentGridBothSidesTests : BunitContext, IAsyncLifetime
{
    private const string ApiBaseUrl = "https://api.test.invalid";

    private readonly RecordingApiStub _api = new();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public DocumentGridBothSidesTests()
    {
        // Same setup as InvoicesTypeFilterTests: popover guard off, query-string provider on,
        // real API clients over a recording stub, grid dependencies stubbed.
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(options => options.ResourcesPath = "Resources");
        Services.AddSupplyValueFromQueryProvider();

        var httpClient = new HttpClient(_api) { BaseAddress = new Uri(ApiBaseUrl) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);
        Services.AddSingleton(Substitute.For<AuthenticationStateProvider>());
        Services.AddSingleton<FakvioService>();
        Services.AddSingleton<FileAttachmentApiService>();
        Services.AddSingleton<ClientApiService>();
        Services.AddSingleton<InvoiceTemplateApiService>();
        Services.AddSingleton<ContentTemplateApiService>();
        Services.AddSingleton<AlertApiService>();
        Services.AddSingleton(Substitute.For<IUiErrorHandler>());

        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton<GridStateService>();
        var preferencesState = Substitute.For<UserPreferencesState>();
        preferencesState.Preferences.Returns(new UserPreferencesDto());
        preferencesState.EnsureLoadedAsync().Returns(new UserPreferencesDto());
        Services.AddSingleton(preferencesState);

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Regression: the grid mounts while the page is still loading its reference data. Its very
    /// first request must already carry the document type, otherwise the list shows every issued
    /// type mixed together (an untyped request = "all issued") and nothing reloads it afterwards.
    /// </summary>
    [Theory]
    [InlineData("/invoices", "Invoice")]
    [InlineData("/invoices?type=Proforma", "Proforma")]
    [InlineData("/received-invoices", "ReceivedInvoice")]
    public async Task First_grid_request_already_carries_the_document_type(string path, string expectedType)
    {
        _api.ReferenceDataDelay = TimeSpan.FromMilliseconds(150);

        var cut = RenderAt(path);
        cut.WaitForAssertion(() => PagedRequestTypes().ShouldNotBeEmpty());

        // Let the delayed reference data arrive, so a (wrong) late reload would be recorded too.
        await Task.Delay(600);

        PagedRequestTypes().ShouldAllBe(t => t == expectedType);
    }

    /// <summary>DocumentType of EVERY paged request, "" when the request had none.</summary>
    private IReadOnlyList<string> PagedRequestTypes() =>
        _api.Requests
            .Where(r => r.Uri.AbsolutePath.Contains("/api/invoice/paged", StringComparison.Ordinal))
            .Select(r => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["DocumentType"] ?? "")
            .ToList();

    // ── helpers ─────────────────────────────────────────────────────────────

    private IStringLocalizer<SharedResource> L => Services.GetRequiredService<IStringLocalizer<SharedResource>>();

    private NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<Invoices> RenderAt(string path)
    {
        Nav.NavigateTo($"{ApiBaseUrl}{path}");
        return Render<Invoices>();
    }

    private static InvoiceDto Row(long id, EDocumentType type, EInvoiceStatus status, int attachments = 0,
        DateTime? due = null) => new()
    {
        Id = id,
        DocumentNumber = $"DOC{id}",
        ClientName = $"Party {id}",
        DocumentType = type,
        Status = status,
        AttachmentCount = attachments,
        DueDate = due,
        CurrencyCode = "CZK"
    };

    /// <summary>Renders the page and waits until the grid has shown the given number of data rows.</summary>
    private IRenderedComponent<Invoices> RenderWithRows(string path, params InvoiceDto[] rows)
    {
        foreach (var group in rows.GroupBy(r => r.DocumentType))
            _api.RowsByType[group.Key.ToString()] = group.ToList();

        var cut = RenderAt(path);
        cut.WaitForAssertion(() => DataRows(cut).Count.ShouldBe(rows.Length));
        return cut;
    }

    private static IReadOnlyList<IElement> DataRows(IRenderedComponent<Invoices> cut) =>
        cut.FindAll("tbody tr.mud-table-row").Where(r => r.QuerySelector("td") is not null).ToList();

    private static IReadOnlyList<string> HeaderTexts(IRenderedComponent<Invoices> cut) =>
        cut.FindAll("th").Select(h => h.TextContent.Trim()).ToList();

    private static IElement? RowButton(IElement row, string title) =>
        row.QuerySelectorAll("button").FirstOrDefault(b => b.GetAttribute("title") == title);

    private static IElement? ToolbarButton(IRenderedComponent<Invoices> cut, string text) =>
        cut.FindAll("button").FirstOrDefault(b => b.TextContent.Trim() == text);

    private void SelectAllRows(IRenderedComponent<Invoices> cut)
    {
        // Header checkbox = select all rows of the page (MudBlazor SelectColumn).
        cut.Find("thead input[type=checkbox]").Change(true);
        cut.WaitForAssertion(() => cut.Markup.ShouldContain(L["Bulk_Selected"].Value));
    }

    private IReadOnlyList<string> RequestedDocumentTypes() =>
        _api.Requests
            .Where(r => r.Uri.AbsolutePath.Contains("/api/invoice/paged", StringComparison.Ordinal))
            .Select(r => System.Web.HttpUtility.ParseQueryString(r.Uri.Query)["DocumentType"])
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();

    // Material icons are svg fragments; the browser DOM re-serializes them, so compare on the
    // path data of the icon instead of the raw string. Used to prove a row action is absent
    // without depending on tooltips.
    private static string IconMarker(string svg) => Regex.Matches(svg, "d=\"([^\"]+)\"").Last().Groups[1].Value;

    private static readonly string PdfIcon = IconMarker(Icons.Material.Filled.PictureAsPdf);
    private static readonly string EmailIcon = IconMarker(Icons.Material.Filled.Email);
    private static readonly string CopyIcon = IconMarker(Icons.Material.Filled.ContentCopy);
    private static readonly string MoreIcon = IconMarker(Icons.Material.Filled.MoreVert);
    private static readonly string DropdownIcon = IconMarker(Icons.Material.Filled.ArrowDropDown);

    private static IElement ToolbarDropdown(IRenderedComponent<Invoices> cut) =>
        cut.FindAll(".mud-menu button").First(b => b.InnerHtml.Contains(DropdownIcon));

    // ── request ─────────────────────────────────────────────────────────────

    [Fact]
    public void Received_route_requests_the_unified_endpoint_with_ReceivedInvoice()
    {
        RenderWithRows("/received-invoices", Row(1, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft));

        RequestedDocumentTypes().ShouldContain("ReceivedInvoice");
        _api.Requests.ShouldNotContain(r => r.Uri.AbsolutePath.Contains("/api/received-invoice/paged"),
            "the legacy list endpoint must no longer be used by the grid");
    }

    // ── columns ─────────────────────────────────────────────────────────────

    [Fact]
    public void Received_columns_have_supplier_and_attachments_but_no_alert_or_email()
    {
        var cut = RenderWithRows("/received-invoices", Row(1, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft));
        var headers = HeaderTexts(cut);

        headers.ShouldContain(L["ReceivedInvoice_Supplier"].Value);
        headers.ShouldContain(L["ReceivedInvoice_Attachments"].Value);
        headers.ShouldNotContain(L["Invoice_Client"].Value);
        headers.ShouldNotContain(L["Invoice_EmailColumn"].Value);
        // The alert column has an empty title and a 32px header: it must not exist on this side.
        cut.FindAll("th[style*='width:32px']").ShouldBeEmpty();
    }

    [Fact]
    public void Issued_columns_stay_unchanged()
    {
        var cut = RenderWithRows("/invoices", Row(1, EDocumentType.Invoice, EInvoiceStatus.Draft));
        var headers = HeaderTexts(cut);

        headers.ShouldContain(L["Invoice_Client"].Value);
        headers.ShouldContain(L["Invoice_EmailColumn"].Value);
        headers.ShouldNotContain(L["ReceivedInvoice_Supplier"].Value);
        headers.ShouldNotContain(L["ReceivedInvoice_Attachments"].Value);
        cut.FindAll("th[style*='width:32px']").ShouldNotBeEmpty();
    }

    // ── row actions ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EInvoiceStatus.Draft, true, false, true, false)]
    [InlineData(EInvoiceStatus.Completed, false, true, false, false)]
    [InlineData(EInvoiceStatus.PartiallyPaid, false, true, false, false)]
    [InlineData(EInvoiceStatus.Paid, false, false, false, false)]
    [InlineData(EInvoiceStatus.Deleted, false, false, false, true)]
    public void Received_row_actions_follow_the_status(
        EInvoiceStatus status, bool approve, bool markPaid, bool delete, bool restore)
    {
        var cut = RenderWithRows("/received-invoices", Row(1, EDocumentType.ReceivedInvoice, status));
        var row = DataRows(cut).Single();

        (RowButton(row, L["ReceivedInvoice_Approve"].Value) is not null).ShouldBe(approve, "approve");
        (RowButton(row, L["Invoice_MarkAsPaid"].Value) is not null).ShouldBe(markPaid, "mark paid");
        (RowButton(row, L["Btn_Delete"].Value) is not null).ShouldBe(delete, "delete");
        (RowButton(row, L["Invoice_Restore"].Value) is not null).ShouldBe(restore, "restore");
        // There is no "Reject" action any more.
        RowButton(row, L["ReceivedInvoice_Reject"].Value).ShouldBeNull();
    }

    [Fact]
    public void Received_row_has_no_pdf_email_copy_or_template_actions()
    {
        var cut = RenderWithRows("/received-invoices", Row(1, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft));
        var rowHtml = DataRows(cut).Single().InnerHtml;

        rowHtml.ShouldNotContain(PdfIcon);
        rowHtml.ShouldNotContain(EmailIcon);
        rowHtml.ShouldNotContain(CopyIcon);
        rowHtml.ShouldNotContain(MoreIcon);
    }

    [Fact]
    public void Issued_row_keeps_pdf_email_copy_and_template_actions()
    {
        var cut = RenderWithRows("/invoices", Row(1, EDocumentType.Invoice, EInvoiceStatus.Completed));
        var rowHtml = DataRows(cut).Single().InnerHtml;

        rowHtml.ShouldContain(PdfIcon);
        rowHtml.ShouldContain(EmailIcon);
        rowHtml.ShouldContain(CopyIcon);
        rowHtml.ShouldContain(MoreIcon);
    }

    // ── status chip texts ───────────────────────────────────────────────────

    [Fact]
    public void Received_status_chips_use_received_and_approved_wording()
    {
        var cut = RenderWithRows("/received-invoices",
            Row(1, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft),
            Row(2, EDocumentType.ReceivedInvoice, EInvoiceStatus.Completed),
            Row(3, EDocumentType.ReceivedInvoice, EInvoiceStatus.PartiallyPaid));

        var chips = cut.FindAll("td .mud-chip").Select(c => c.TextContent.Trim()).ToList();
        chips.ShouldContain(L["EReceivedInvoiceStatus_Received"].Value);
        chips.ShouldContain(L["EReceivedInvoiceStatus_Approved"].Value);
        chips.ShouldContain(L["Invoice_StatusPartiallyPaid"].Value);
        chips.ShouldNotContain(L["Invoice_StatusDraft"].Value);
    }

    [Fact]
    public void Issued_status_chips_use_issued_wording_and_partially_paid_has_a_label()
    {
        var cut = RenderWithRows("/invoices",
            Row(1, EDocumentType.Invoice, EInvoiceStatus.Draft),
            Row(2, EDocumentType.Invoice, EInvoiceStatus.Completed),
            Row(3, EDocumentType.Invoice, EInvoiceStatus.PartiallyPaid));

        var chips = cut.FindAll("td .mud-chip").Select(c => c.TextContent.Trim()).ToList();
        chips.ShouldContain(L["Invoice_StatusDraft"].Value);
        chips.ShouldContain(L["Invoice_StatusIssued"].Value);
        chips.ShouldContain(L["Invoice_StatusPartiallyPaid"].Value);
    }

    // ── overdue highlight ───────────────────────────────────────────────────

    [Theory]
    [InlineData("/received-invoices", EDocumentType.ReceivedInvoice)]
    [InlineData("/invoices", EDocumentType.Invoice)]
    public void Due_date_is_red_only_for_unpaid_open_documents_past_due(string path, EDocumentType type)
    {
        var past = DateTime.UtcNow.AddDays(-10);
        var cut = RenderWithRows(path,
            Row(1, type, EInvoiceStatus.Completed, due: past),
            Row(2, type, EInvoiceStatus.PartiallyPaid, due: past),
            Row(3, type, EInvoiceStatus.Paid, due: past),
            Row(4, type, EInvoiceStatus.Completed, due: DateTime.UtcNow.AddDays(10)));

        bool IsRed(int rowIndex) => DataRows(cut)[rowIndex].QuerySelectorAll(".mud-error-text").Length > 0;

        IsRed(0).ShouldBeTrue("completed and past due");
        IsRed(1).ShouldBeTrue("partially paid and past due");
        IsRed(2).ShouldBeFalse("paid");
        IsRed(3).ShouldBeFalse("not yet due");
    }

    // ── bulk toolbar ────────────────────────────────────────────────────────

    [Fact]
    public void Received_bulk_toolbar_has_approve_pay_delete_attachments_isdoc_only()
    {
        var cut = RenderWithRows("/received-invoices", Row(1, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft));
        SelectAllRows(cut);

        ToolbarButton(cut, L["ReceivedInvoice_Approve"].Value).ShouldNotBeNull();
        ToolbarButton(cut, L["Bulk_MarkPaid"].Value).ShouldNotBeNull();
        ToolbarButton(cut, L["Bulk_Delete"].Value).ShouldNotBeNull();
        ToolbarButton(cut, L["ReceivedInvoice_DownloadAttachments"].Value).ShouldNotBeNull();
        ToolbarButton(cut, L["Bulk_Isdoc"].Value).ShouldNotBeNull();
        ToolbarButton(cut, L["Bulk_ClearSelection"].Value).ShouldNotBeNull();

        ToolbarButton(cut, L["Bulk_Pdf"].Value).ShouldBeNull();
        ToolbarButton(cut, L["Bulk_Ubl"].Value).ShouldBeNull();
        ToolbarButton(cut, L["Bulk_Email"].Value).ShouldBeNull();
        ToolbarButton(cut, L["Bulk_Issue"].Value).ShouldBeNull();
    }

    [Fact]
    public void Issued_bulk_toolbar_is_unchanged()
    {
        var cut = RenderWithRows("/invoices", Row(1, EDocumentType.Invoice, EInvoiceStatus.Draft));
        SelectAllRows(cut);

        foreach (var key in new[] { "Bulk_Issue", "Bulk_MarkPaid", "Bulk_Pdf", "Bulk_Isdoc", "Bulk_Ubl", "Bulk_Email", "Bulk_Delete" })
            ToolbarButton(cut, L[key].Value).ShouldNotBeNull(key);

        ToolbarButton(cut, L["ReceivedInvoice_DownloadAttachments"].Value).ShouldBeNull();
    }

    [Fact]
    public void Received_bulk_approve_posts_to_the_unified_bulk_complete_endpoint()
    {
        JSInterop.Setup<bool>("confirm", _ => true).SetResult(true);
        var cut = RenderWithRows("/received-invoices", Row(7, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft));
        SelectAllRows(cut);

        ToolbarButton(cut, L["ReceivedInvoice_Approve"].Value)!.Click();

        cut.WaitForAssertion(() =>
        {
            var post = _api.Requests.SingleOrDefault(r => r.Method == HttpMethod.Post
                && r.Uri.AbsolutePath == "/api/invoice/bulk/complete");
            post.ShouldNotBeNull();
            post.Body.ShouldContain("7");
        });
    }

    [Fact]
    public void Received_bulk_isdoc_uses_the_received_endpoint()
    {
        var cut = RenderWithRows("/received-invoices", Row(7, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft));
        SelectAllRows(cut);

        ToolbarButton(cut, L["Bulk_Isdoc"].Value)!.Click();

        cut.WaitForAssertion(() => _api.Requests.ShouldContain(r =>
            r.Uri.AbsolutePath == "/api/received-invoice/bulk/isdoc" && r.Uri.Query.Contains("ids=7")));
    }

    [Fact]
    public void Bulk_attachment_download_skips_rows_without_attachments()
    {
        var cut = RenderWithRows("/received-invoices",
            Row(1, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft, attachments: 0),
            Row(2, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft, attachments: 3));
        SelectAllRows(cut);

        ToolbarButton(cut, L["ReceivedInvoice_DownloadAttachments"].Value)!.Click();

        cut.WaitForAssertion(() =>
        {
            var get = _api.Requests.Single(r => r.Uri.AbsolutePath == "/api/received-invoice/bulk/attachments");
            get.Uri.Query.ShouldContain("ids=2");
            get.Uri.Query.ShouldNotContain("1,");
        });
        JSInterop.Invocations.ShouldContain(i => i.Identifier == "downloadFile"
            && ((string)i.Arguments[0]!).StartsWith("Prilohy_"));
    }

    // ── attachment download from a row ──────────────────────────────────────

    [Fact]
    public void Single_attachment_downloads_the_file_directly_with_its_original_name()
    {
        _api.Attachments = [new FileAttachmentDto { Id = 55, OriginalFileName = "scan.pdf", ContentType = "application/pdf" }];
        var cut = RenderWithRows("/received-invoices",
            Row(9, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft, attachments: 1));

        RowButton(DataRows(cut).Single(), L["ReceivedInvoice_DownloadAttachments"].Value)!.Click();

        cut.WaitForAssertion(() => JSInterop.Invocations.ShouldContain(i =>
            i.Identifier == "downloadFile" && (string)i.Arguments[0]! == "scan.pdf"));
        _api.Requests.ShouldContain(r => r.Uri.AbsolutePath == "/api/file-attachment/ReceivedInvoice/9");
        _api.Requests.ShouldContain(r => r.Uri.AbsolutePath == "/api/file-attachment/55/download");
        _api.Requests.ShouldNotContain(r => r.Uri.AbsolutePath.EndsWith("download-all"));
    }

    [Fact]
    public void Several_attachments_download_as_a_zip_named_by_the_document_number()
    {
        var cut = RenderWithRows("/received-invoices",
            Row(9, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft, attachments: 2));

        RowButton(DataRows(cut).Single(), L["ReceivedInvoice_DownloadAttachments"].Value)!.Click();

        cut.WaitForAssertion(() => JSInterop.Invocations.ShouldContain(i =>
            i.Identifier == "downloadFile" && (string)i.Arguments[0]! == "Prilohy_DOC9.zip"));
        _api.Requests.ShouldContain(r => r.Uri.AbsolutePath == "/api/file-attachment/ReceivedInvoice/9/download-all");
    }

    // ── toolbar urls ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/received-invoices", "/received-invoices/create", null)]
    [InlineData("/received-invoices", null, "/received-invoices/import?target=ReceivedInvoice")]
    [InlineData("/invoices", "/invoices/create", null)]
    [InlineData("/invoices", null, "/invoices/import?target=IssuedInvoice")]
    [InlineData("/invoices?type=CreditNote", "/invoices/create?type=CreditNote", null)]
    [InlineData("/invoices?type=CreditNote", null, "/invoices/import?target=IssuedInvoice")]
    public void New_and_import_go_to_the_urls_of_the_side(string path, string? createUrl, string? importUrl)
    {
        // Menu items live in the popover provider, which the page itself does not render.
        var popover = Render<MudPopoverProvider>();
        var cut = RenderAt(path);
        var policy = PolicyFor(path);

        cut.WaitForAssertion(() => ToolbarButton(cut, L[policy.NewLabelKey].Value).ShouldNotBeNull());

        // Both targets come from the policy of the page as it was rendered. Each click is checked on a page
        // that has not been navigated in between: navigating away and back makes the page re-apply its route
        // while the test is already clicking, which made this test fail now and then.
        if (importUrl is not null)
        {
            // Import lives in the dropdown next to "New".
            ToolbarDropdown(cut).Click();
            popover.WaitForAssertion(() => popover.Markup.ShouldContain(L["Nav_ImportInvoices"].Value));
            popover.FindAll(".mud-menu-item")
                .First(i => i.TextContent.Contains(L["Nav_ImportInvoices"].Value)).Click();
            popover.WaitForAssertion(() => Nav.Uri.ShouldEndWith(importUrl));
            return;
        }

        // "New" is the primary toolbar button.
        ToolbarButton(cut, L[policy.NewLabelKey].Value)!.Click();
        Nav.Uri.ShouldEndWith(createUrl!);
    }

    [Theory]
    [InlineData("/received-invoices", false)]
    [InlineData("/invoices", true)]
    public void From_template_entry_exists_only_for_issued_documents(string path, bool expected)
    {
        var popover = Render<MudPopoverProvider>();
        var cut = RenderAt(path);
        cut.WaitForAssertion(() => ToolbarDropdown(cut).ShouldNotBeNull());

        ToolbarDropdown(cut).Click();
        popover.WaitForAssertion(() => popover.Markup.ShouldContain(L["Nav_ImportInvoices"].Value));

        popover.Markup.Contains(L["Invoice_NewFromTemplate"].Value).ShouldBe(expected);
    }

    private static DocumentTypeUiPolicy PolicyFor(string pathAndQuery)
    {
        var uri = new Uri($"{ApiBaseUrl}{pathAndQuery}");
        return DocumentTypeUiPolicy.ForListRoute(
            uri.AbsolutePath, System.Web.HttpUtility.ParseQueryString(uri.Query)["type"])!;
    }

    // ── navigation on the same instance ─────────────────────────────────────

    [Fact]
    public void Navigating_between_sides_reuses_the_instance_reloads_and_clears_the_selection()
    {
        var cut = RenderWithRows("/invoices", Row(1, EDocumentType.Invoice, EInvoiceStatus.Draft));
        var instanceBefore = cut.Instance;
        SelectAllRows(cut);

        // Issued -> received
        _api.RowsByType["ReceivedInvoice"] = [Row(2, EDocumentType.ReceivedInvoice, EInvoiceStatus.Draft)];
        cut.InvokeAsync(() => Nav.NavigateTo($"{ApiBaseUrl}/received-invoices"));

        cut.Instance.ShouldBeSameAs(instanceBefore, "the test is only meaningful while the component is NOT recreated");
        cut.WaitForAssertion(() => RequestedDocumentTypes().ShouldContain("ReceivedInvoice"));
        cut.WaitForAssertion(() => cut.Markup.ShouldNotContain(L["Bulk_Selected"].Value));
        cut.WaitForAssertion(() => HeaderTexts(cut).ShouldContain(L["ReceivedInvoice_Supplier"].Value));

        // Received -> issued credit notes
        _api.RowsByType["CreditNote"] = [Row(3, EDocumentType.CreditNote, EInvoiceStatus.Draft)];
        cut.InvokeAsync(() => Nav.NavigateTo($"{ApiBaseUrl}/invoices?type=CreditNote"));

        cut.WaitForAssertion(() => RequestedDocumentTypes().ShouldContain("CreditNote"));
        cut.WaitForAssertion(() => HeaderTexts(cut).ShouldContain(L["Invoice_Client"].Value));
        cut.Markup.ShouldNotContain(L["Bulk_Selected"].Value);
        cut.Instance.ShouldBeSameAs(instanceBefore);
    }

    // ── API stub ────────────────────────────────────────────────────────────

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

    /// <summary>
    /// Answers the endpoints the grid uses with plausible payloads and records every request.
    /// Unknown paths get an empty collection so loaders that swallow errors cannot skew a test.
    /// </summary>
    private sealed class RecordingApiStub : HttpMessageHandler
    {
        private readonly List<RecordedRequest> _requests = [];

        /// <summary>Rows the paged endpoint returns, keyed by the DocumentType query value.</summary>
        public Dictionary<string, List<InvoiceDto>> RowsByType { get; } = new();

        /// <summary>Attachments returned for a single-record attachment listing.</summary>
        public List<FileAttachmentDto> Attachments { get; set; } = [];

        /// <summary>Delay of every request except the paged list (see SendAsync).</summary>
        public TimeSpan ReferenceDataDelay { get; set; }

        public IReadOnlyList<RecordedRequest> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests) _requests.Add(new RecordedRequest(request.Method, uri, body));

            var path = uri.AbsolutePath;

            // Reference data answers late when a test asks for it, so the page renders (and the
            // grid mounts) while OnInitializedAsync is still waiting - as it does in the browser.
            if (ReferenceDataDelay > TimeSpan.Zero && !path.Contains("/api/invoice/paged", StringComparison.Ordinal))
                await Task.Delay(ReferenceDataDelay, cancellationToken);

            // Binary downloads (attachment file, ZIPs).
            if (path.EndsWith("/download", StringComparison.Ordinal)
                || path.EndsWith("/download-all", StringComparison.Ordinal)
                || path.StartsWith("/api/received-invoice/bulk/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }

            string json;
            if (path.Contains("/api/invoice/paged", StringComparison.Ordinal))
            {
                var type = System.Web.HttpUtility.ParseQueryString(uri.Query)["DocumentType"] ?? "";
                var items = RowsByType.TryGetValue(type, out var rows) ? rows : [];
                json = JsonSerializer.Serialize(new
                {
                    items,
                    totalCount = items.Count,
                    pageNumber = 1,
                    pageSize = 50
                }, JsonSerializerOptions.Web);
            }
            else if (path.Contains("/paged", StringComparison.Ordinal))
            {
                json = """{"items":[],"totalCount":0,"pageNumber":1,"pageSize":50}""";
            }
            else if (path.StartsWith("/api/file-attachment/", StringComparison.Ordinal))
            {
                json = JsonSerializer.Serialize(Attachments, JsonSerializerOptions.Web);
            }
            else if (path.Contains("/bulk/", StringComparison.Ordinal))
            {
                json = """{"successCount":1,"failedCount":0,"errors":[]}""";
            }
            else
            {
                json = "[]";
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
