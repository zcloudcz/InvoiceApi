// ============================================================================
// DocumentDetailTestBase - shared bUnit setup for the tests of the ONE document detail page
// (InvoiceDetail, which serves /invoices/... AND /received-invoices/...) and of the payments
// panel that sits on it (issue #542, 2/2).
//
// The API is replaced by DetailApiStub: it answers the reference data and the document calls the
// page makes, records every request and can answer reference data with a REAL async delay. The
// delay matters: it makes the page render while OnParametersSetAsync is still awaiting, which is
// the situation in the browser. A stub that answers synchronously hides ordering bugs (a child
// rendered before the document type is known).
//
// Expected texts are resolved through the same localizer the page uses, so the tests do not
// depend on the machine's UI culture.
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Currency;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Contracts.Dto.ReverseChargeCode;
using Fakvio.Contracts.Dto.VatRate;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;

namespace Fakvio.Tests.Unit;

/// <summary>One request the page sent to the stub API.</summary>
public sealed record DetailRequest(HttpMethod Method, Uri Uri, string Body)
{
    public string Path => Uri.AbsolutePath;

    /// <summary>The JSON body as a document (an empty body = an empty object).</summary>
    public JsonElement Json => JsonDocument.Parse(string.IsNullOrWhiteSpace(Body) ? "{}" : Body).RootElement;
}

/// <summary>Recording stub of the API used by the document detail page and its payments panel.</summary>
public sealed class DetailApiStub : HttpMessageHandler
{
    private readonly List<DetailRequest> _requests = [];

    public static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    /// <summary>Documents served by GET /api/invoice/{id}; transitions and updates change them in place.</summary>
    public Dictionary<long, InvoiceDto> Documents { get; } = new();

    /// <summary>Rows served by GET /api/invoice/{id}/payments, by document id.</summary>
    public Dictionary<long, List<PaymentMatchDto>> Payments { get; } = new();

    /// <summary>Suppliers/clients offered by the select (id 5 = "Supplier Ltd.").</summary>
    public List<ClientDto> Clients { get; } =
    [
        new ClientDto { Id = 5, CompanyName = "Supplier Ltd.", IsActive = true },
        new ClientDto { Id = 6, CompanyName = "Customer Ltd.", IsActive = true }
    ];

    /// <summary>The issuer (our company); IsVatPayer drives the VAT column of an ISSUED document.</summary>
    public ClientDto Issuer { get; set; } = new() { Id = 1, CompanyName = "Our Company", IsIssuer = true, IsVatPayer = true, IsActive = true };

    /// <summary>Delay of every reference-data call (not of the document itself).</summary>
    public TimeSpan ReferenceDelay { get; set; }

    /// <summary>When set, POST .../unmatch answers 400 with this message.</summary>
    public string? UnmatchError { get; set; }

    /// <summary>Id given to a document created by POST /api/invoice.</summary>
    public long CreatedId { get; set; } = 77;

    public IReadOnlyList<DetailRequest> Requests
    {
        get { lock (_requests) return _requests.ToList(); }
    }

    public IReadOnlyList<DetailRequest> Writes =>
        Requests.Where(r => r.Method != HttpMethod.Get).ToList();

    public bool Called(HttpMethod method, string path) =>
        Requests.Any(r => r.Method == method && r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_requests) _requests.Add(new DetailRequest(request.Method, uri, body));

        var path = uri.AbsolutePath.ToLowerInvariant();
        var isDocumentGet = request.Method == HttpMethod.Get
                            && System.Text.RegularExpressions.Regex.IsMatch(path, @"^/api/invoice/\d+$");

        if (ReferenceDelay > TimeSpan.Zero && !isDocumentGet)
            await Task.Delay(ReferenceDelay, ct);

        return Route(request, path, body);
    }

    private HttpResponseMessage Route(HttpRequestMessage request, string path, string body)
    {
        // ── the document itself and its transitions ────────────────────────
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^/api/invoice/(\d+)(/[a-z-]+)?$");
        if (m.Success)
        {
            var id = long.Parse(m.Groups[1].Value);
            var action = m.Groups[2].Value;

            if (request.Method == HttpMethod.Get && action == "/payments")
                return Ok(Payments.TryGetValue(id, out var rows) ? rows : []);

            if (!Documents.TryGetValue(id, out var doc))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Get && action == "")
                return Ok(doc);

            if (request.Method == HttpMethod.Delete)
            {
                doc.Status = EInvoiceStatus.Deleted;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (request.Method == HttpMethod.Put)
            {
                var update = JsonSerializer.Deserialize<UpdateInvoiceDto>(body, Json)!;
                if (update.DocumentNumber is not null) doc.DocumentNumber = update.DocumentNumber;
                return Ok(doc);
            }

            if (request.Method == HttpMethod.Post)
            {
                switch (action)
                {
                    case "/complete": doc.Status = EInvoiceStatus.Completed; break;
                    case "/mark-paid": doc.Status = EInvoiceStatus.Paid; break;
                    case "/mark-unpaid": doc.Status = EInvoiceStatus.Completed; break;
                    case "/revert-to-draft": doc.Status = EInvoiceStatus.Draft; break;
                    case "/restore": doc.Status = EInvoiceStatus.Draft; break;
                }
                return Ok(doc);
            }
        }

        if (path == "/api/invoice" && request.Method == HttpMethod.Post)
        {
            var create = JsonSerializer.Deserialize<CreateInvoiceDto>(body, Json)!;
            var created = new InvoiceDto { Id = CreatedId, DocumentType = create.DocumentType, Status = EInvoiceStatus.Draft };
            Documents[CreatedId] = created;
            return Ok(created);
        }

        // ── payments ───────────────────────────────────────────────────────
        if (path.StartsWith("/api/payment-matching/transactions/", StringComparison.Ordinal) && path.EndsWith("/unmatch", StringComparison.Ordinal))
        {
            return UnmatchError is null
                ? Ok(new { })
                : new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { message = UnmatchError }), Encoding.UTF8, "application/json")
                };
        }

        // ── received ISDOC (legacy endpoint, bytes) ────────────────────────
        if (path.EndsWith("/isdoc", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };

        // ── reference data ─────────────────────────────────────────────────
        if (path.StartsWith("/api/client/issuer", StringComparison.Ordinal)) return Ok(Issuer);
        if (path.StartsWith("/api/client", StringComparison.Ordinal)) return Ok(Clients);
        if (path.StartsWith("/api/currency/active", StringComparison.Ordinal))
            return Ok(new List<CurrencyDto> { new() { Id = 1, Code = "CZK", Name = "Koruna" }, new() { Id = 2, Code = "EUR", Name = "Euro" } });
        if (path.StartsWith("/api/vatrate", StringComparison.Ordinal))
            return Ok(new List<VatRateDto>
            {
                new() { Id = 1, Rate = 21, IsDefault = true, IsActive = true, Name = "Basic" },
                new() { Id = 2, Rate = 12, IsReduced = true, IsActive = true, Name = "Reduced" }
            });
        if (path.StartsWith("/api/reversechargecode", StringComparison.Ordinal)) return Ok(new List<ReverseChargeCodeDto>());

        // Lists (content templates, alerts, attachments, ...) are empty; anything else is "not found",
        // which the page treats as "nothing to show" (number sequence preview, QR, ...).
        if (path.StartsWith("/api/content-template", StringComparison.Ordinal)
            || path.StartsWith("/api/alert", StringComparison.Ordinal)
            || path.StartsWith("/api/file-attachment/", StringComparison.Ordinal))
            return Ok(new List<object>());

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Ok(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json")
    };
}

/// <summary>Base class: services, stub API, helpers shared by the document detail tests.</summary>
public abstract class DocumentDetailTestBase : BunitContext, IAsyncLifetime
{
    protected const string ApiBaseUrl = "https://api.test.invalid";

    protected readonly DetailApiStub Api = new();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    /// <summary>Answer the "are you sure?" message boxes (mark unpaid, unmatch) with this value.</summary>
    protected bool? ConfirmAnswer { get; set; } = true;

    protected DocumentDetailTestBase()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(options => options.ResourcesPath = "Resources");
        Services.AddSupplyValueFromQueryProvider();

        var httpClient = new HttpClient(Api) { BaseAddress = new Uri(ApiBaseUrl) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);
        Services.AddSingleton(Substitute.For<AuthenticationStateProvider>());
        Services.AddSingleton<FakvioService>();
        Services.AddSingleton<ClientApiService>();
        Services.AddSingleton<CurrencyApiService>();
        Services.AddSingleton<VatRateApiService>();
        Services.AddSingleton<ReverseChargeCodeApiService>();
        Services.AddSingleton<InvoiceTemplateApiService>();
        Services.AddSingleton<ContentTemplateApiService>();
        Services.AddSingleton<NumberSequenceApiService>();
        Services.AddSingleton<AlertApiService>();
        Services.AddSingleton<FileAttachmentApiService>();
        Services.AddSingleton<PaymentMatchingApiService>();
        Services.AddSingleton(_snackbar);
        Services.AddSingleton(Substitute.For<IUiErrorHandler>());
        Services.AddSingleton(Substitute.For<ILocalStorageService>());

        // A message box answers ConfirmAnswer without needing a dialog provider in the render tree.
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowMessageBox(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DialogOptions>())
            .Returns(_ => Task.FromResult(ConfirmAnswer));
        Services.AddSingleton(dialogs);

        // The banner has its own tests; here only its presence matters.
        ComponentFactories.AddStub<ReadinessBanner>();

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected IStringLocalizer<SharedResource> L => Services.GetRequiredService<IStringLocalizer<SharedResource>>();

    protected NavigationManager Nav => Services.GetRequiredService<NavigationManager>();

    private readonly ISnackbar _snackbar = Substitute.For<ISnackbar>();

    /// <summary>Texts of all snackbars shown so far (the snackbar is a recording substitute).</summary>
    protected IReadOnlyList<string> SnackbarTexts =>
        _snackbar.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "Add")
            .Select(c => c.GetArguments()[0]?.ToString() ?? "")
            .ToList();

    /// <summary>A received document as the API returns it.</summary>
    protected static InvoiceDto Received(long id, EInvoiceStatus status, string? number = "REC-1") => new()
    {
        Id = id,
        DocumentType = EDocumentType.ReceivedInvoice,
        Status = status,
        DocumentNumber = number,
        ClientId = 5,
        ClientName = "Supplier Ltd.",
        IssuerId = 1,
        IssuerName = "Our Company",
        CurrencyId = 1,
        CurrencyCode = "CZK",
        CurrencySymbol = "Kc",
        IssueDate = new DateTime(2026, 9, 1),
        DueDate = new DateTime(2026, 9, 15),
        TaxableSupplyDate = new DateTime(2026, 9, 1),
        ReceivedDate = new DateTime(2026, 9, 2),
        VariableSymbol = "VS-ABC-123",
        BankAccountNumber = "123/0100",
        IBAN = "CZ6501000000000000000123",
        SWIFT = "KOMBCZPP",
        TotalBeforeVat = 1000m,
        TotalVat = 150m,
        TotalWithVat = 1150m,
        InvoiceItem =
        [
            new InvoiceItemDto
            {
                OrderIndex = 1, Description = "Odd rate item", Quantity = 1, Unit = "ks", UnitPrice = 1000m,
                VatRateId = null, VatRatePercentage = 15m, VatRegime = EVatRegime.Standard,
                TotalBeforeVat = 1000m, VatAmount = 150m, TotalWithVat = 1150m
            }
        ]
    };

    /// <summary>An issued document as the API returns it.</summary>
    protected static InvoiceDto Issued(long id, EInvoiceStatus status, EDocumentType type = EDocumentType.Invoice) => new()
    {
        Id = id,
        DocumentType = type,
        Status = status,
        DocumentNumber = "2026-0001",
        ClientId = 6,
        ClientName = "Customer Ltd.",
        IssuerId = 1,
        IssuerName = "Our Company",
        CurrencyId = 1,
        CurrencyCode = "CZK",
        CurrencySymbol = "Kc",
        TotalWithVat = 1210m,
        InvoiceItem =
        [
            new InvoiceItemDto
            {
                OrderIndex = 1, Description = "Work", Quantity = 1, Unit = "ks", UnitPrice = 1000m,
                VatRateId = 1, VatRatePercentage = 21m, VatRegime = EVatRegime.Standard, TotalWithVat = 1210m
            }
        ]
    };

    /// <summary>All button texts of the rendered page (trimmed), for "exactly these buttons" assertions.</summary>
    protected static IReadOnlyList<string> ButtonTexts(IRenderedComponent<IComponent> cut) =>
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Where(t => t.Length > 0).ToList();

    protected static AngleSharp.Dom.IElement Button(IRenderedComponent<IComponent> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == text);

    /// <summary>
    /// Finds the button and clicks it in ONE step on the renderer thread. A separate Find + Click can race with a
    /// background re-render of the page (QR code, templates...) and then fails with "no event handler with ID".
    /// </summary>
    protected static void Click(IRenderedComponent<IComponent> cut, string text) =>
        cut.InvokeAsync(() => Button(cut, text).Click()).GetAwaiter().GetResult();

    /// <summary>Same as <see cref="Click"/> for a CSS selector (first match).</summary>
    protected static void ClickFirst(IRenderedComponent<IComponent> cut, string selector) =>
        cut.InvokeAsync(() => cut.Find(selector).Click()).GetAwaiter().GetResult();

    /// <summary>Sets the value of the text field with the given label (the component raises ValueChanged like a typing user).</summary>
    protected static async Task SetText(IRenderedComponent<IComponent> cut, string label, string value)
    {
        var field = cut.FindComponents<MudTextField<string>>().First(f => f.Instance.Label == label);
        await cut.InvokeAsync(() => field.Instance.ValueChanged.InvokeAsync(value));
    }

    /// <summary>Picks a supplier/client in the first select labelled <paramref name="label"/>.</summary>
    protected static async Task PickParty(IRenderedComponent<IComponent> cut, string label, long id)
    {
        var select = cut.FindComponents<MudSelect<long?>>().First(s => s.Instance.Label == label);
        await cut.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(id));
    }
}
