using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Contracts.Dto.PaymentMatching;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Components.Shared;
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

/// <summary>Exercises real page handlers and HTTP requests; no copied UI decision logic.</summary>
public class DocumentUxRegressionTests : BunitContext, IAsyncLifetime
{
    private readonly Backend _backend = new();
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;
    private readonly IStringLocalizer<SharedResource> _localizer;
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public DocumentUxRegressionTests()
    {
        Services.AddLogging();
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;
        _localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        _localizer[Arg.Any<string>()].Returns(c => new LocalizedString(c.Arg<string>(), c.Arg<string>()));
        Services.AddSingleton(_localizer);
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton<GridStateService>();
        var preferences = Substitute.For<UserPreferencesState>();
        preferences.Preferences.Returns(new Fakvio.Contracts.Dto.User.UserPreferencesDto());
        preferences.EnsureLoadedAsync().Returns(new Fakvio.Contracts.Dto.User.UserPreferencesDto());
        Services.AddSingleton(preferences);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") });
        Services.AddSingleton(factory);
        foreach (var type in typeof(ApiClientBase).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(ApiClientBase))))
            Services.AddTransient(type);
        _auth = AddAuthorization();
        _auth.SetAuthorized("test@example.test");
        _auth.SetRoles("User");
    }

    [Theory]
    [InlineData("User", false)]
    [InlineData("Admin", true)]
    [InlineData("SysAdmin", true)]
    public void SettingsWrites_MatchApiRoles(string role, bool permitted)
    {
        _auth.SetRoles(role);
        var vat = Render<VatRates>();
        vat.WaitForAssertion(() => vat.FindComponents<ResponsiveButton>().Any(b => b.Instance.Label == "VatRate_New").ShouldBe(permitted));
        var numbering = Render<NumberSequences>();
        numbering.WaitForAssertion(() => numbering.FindComponents<ResponsiveButton>().Any(b => b.Instance.Label == "NumSeq_NewFormat").ShouldBe(permitted));
        var templates = Render<ContentTemplates>();
        templates.WaitForAssertion(() => templates.FindComponents<ResponsiveButton>().Any(b => b.Instance.Label == "ContentTemplate_New").ShouldBe(permitted));
    }

    [Fact]
    public void UserCannotOpenTemplateCreateFormDirectly()
    {
        var cut = Render<ContentTemplateDetail>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Settings_ReadOnly"));
        cut.FindComponents<MudTextField<string>>().ShouldBeEmpty();
    }

    [Fact]
    public async Task PaymentPagers_FetchServerPagesAndResetAfterFilter()
    {
        var cut = Render<Payments>();
        cut.WaitForAssertion(() => cut.FindComponents<MudPagination>().Count.ShouldBe(2));
        await cut.InvokeAsync(() => cut.FindComponents<MudPagination>()[1].Instance.SelectedChanged.InvokeAsync(2));
        _backend.Urls.ShouldContain(u => u.Contains("transactions?") && !u.Contains("status=") && u.Contains("page=2"));
        await cut.InvokeAsync(() => cut.FindComponents<MudPagination>()[0].Instance.SelectedChanged.InvokeAsync(3));
        _backend.Urls.ShouldContain(u => u.Contains("status=Unmatched") && u.Contains("page=3") && u.Contains("pageSize=25"));
        await cut.InvokeAsync(() => cut.FindComponents<MudSelect<EMatchStatus?>>()[0].Instance.ValueChanged.InvokeAsync(EMatchStatus.Matched));
        _backend.Urls.Last(u => u.Contains("transactions?")).ShouldContain("page=1");
    }

    [Fact]
    public void PaymentLoadFailure_IsNotAnEmptyLedger()
    {
        _backend.FailPayments = true;
        var cut = Render<Payments>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Common_LoadFailed"));
        cut.Markup.ShouldNotContain("Payments_Empty");
        cut.Markup.ShouldNotContain("Payments_Unmatched_Empty");
    }

    [Fact]
    public async Task InvoicePaging_CanPropagateFailureForVisibleErrorState()
    {
        _backend.FailInvoices = true;
        var api = Services.GetRequiredService<FakvioService>();
        await Should.ThrowAsync<ApiException>(() => api.GetPagedAsync(throwOnError: true));
        (await api.GetPagedAsync()).Items.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CreateHandlers_RejectConcurrentSubmitAndAllowRetry(bool invoice)
    {
        _backend.HoldWrites = true;
        var page = invoice ? (object)new InvoiceDetail() : new ClientDetail();
        Set(page, "L", _localizer);
        Set(page, "Snackbar", Services.GetRequiredService<ISnackbar>());
        Set(page, invoice ? "InvoiceService" : "ClientService", invoice
            ? Services.GetRequiredService<FakvioService>() : Services.GetRequiredService<ClientApiService>());
        if (invoice)
            Set(page, "_createDto", new CreateInvoiceDto { ClientId = 1, IssuerId = 2, InvoiceItem = [new CreateInvoiceItemDto { Description = "Work", Quantity = 1 }] });
        var method = invoice ? "SaveNewInvoice" : "SaveClient";
        var first = Invoke(page, method);
        await _backend.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Invoke(page, method);
        _backend.Writes.ShouldBe(1);
        _backend.ReleaseWrite.SetResult();
        await first;
        await Invoke(page, method);
        _backend.Writes.ShouldBe(2);
    }

    [Fact]
    public async Task OriginalInvoiceSearch_ForwardsSearchToBothEligibleStatuses()
    {
        var page = new InvoiceDetail();
        Set(page, "InvoiceService", Services.GetRequiredService<FakvioService>());
        var method = typeof(InvoiceDetail).GetMethod("SearchOriginalInvoices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var result = await (Task<IEnumerable<InvoiceDto>>)method.Invoke(page, ["old-2019", CancellationToken.None])!;
        result.ShouldContain(i => i.DocumentNumber == "old-2019");
        _backend.Urls.ShouldContain(u => u.Contains("Search=old-2019") && u.Contains("Status=Completed"));
        _backend.Urls.ShouldContain(u => u.Contains("Search=old-2019") && u.Contains("Status=Paid"));
    }

    [Fact]
    public void InvoiceAmountFilter_IsExplicitlyUnavailable()
    {
        var cut = Render<Invoices>();
        cut.WaitForAssertion(() => cut.FindComponents<PropertyColumn<InvoiceDto, decimal>>()
            .Single(c => c.Instance.Title == "Invoice_TotalWithVat").Instance.Filterable.ShouldBe(false));
    }

    [Fact]
    public void InvoiceFailure_ShowsRetryInsteadOfNoRecords()
    {
        _backend.FailInvoices = true;
        var cut = Render<Invoices>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Common_LoadFailed"));
        cut.Markup.ShouldContain("Common_Retry");
        cut.Markup.ShouldNotContain("Invoice_NoRecords");
    }

    [Fact]
    public void ClientHistory_ProvidesInvoiceLinkAndNoUnsupportedFilters()
    {
        var cut = Render<ClientDetail>(p => p.Add(c => c.Id, 1));
        cut.WaitForAssertion(() => cut.Find("a[href='/invoices/19']").TextContent.ShouldBe("old-2019"));
        cut.FindComponents<PropertyColumn<InvoiceDto, decimal>>()
            .Single(c => c.Instance.Title == "Invoice_TotalWithVat").Instance.Filterable.ShouldBe(false);
        cut.FindComponents<PropertyColumn<InvoiceDto, EInvoiceStatus>>()
            .Single(c => c.Instance.Title == "Label_Status").Instance.Filterable.ShouldBe(false);
    }

    [Fact]
    public async Task InvoiceCreation_HasEmptyClientAndCanSwitchToSearchableCreditNote()
    {
        var cut = Render<InvoiceDetail>();
        cut.WaitForAssertion(() => cut.FindComponents<MudSelect<long?>>()
            .Single(c => c.Instance.Label == "Invoice_Client").Instance.Value.ShouldBeNull());
        var client = cut.FindComponents<MudSelect<long?>>()
            .Single(c => c.Instance.Label == "Invoice_Client").Instance;
        client.Placeholder.ShouldBe("Invoice_SelectClient");
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<EDocumentType>>().Instance.ValueChanged.InvokeAsync(EDocumentType.CreditNote));
        var original = cut.FindComponents<MudAutocomplete<InvoiceDto>>().Single().Instance;
        var results = await cut.InvokeAsync(() => original.SearchFunc("old-2019", CancellationToken.None));
        results.ShouldContain(i => i.DocumentNumber == "old-2019");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClientDeletion_CancelledConfirmationMakesNoDeleteRequest(bool detail)
    {
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ShowMessageBox("Msg_ConfirmDeleteTitle", "Common_DeleteConfirm", "Btn_Delete", null, "Btn_Cancel", null)
            .Returns(Task.FromResult<bool?>(false));
        var page = detail ? (object)new ClientDetail() : new Clients();
        Set(page, "L", _localizer);
        Set(page, "DialogService", dialogs);
        Set(page, "ClientService", Services.GetRequiredService<ClientApiService>());
        if (detail) await Invoke(page, "DeleteClient");
        else await Invoke(page, "DeleteClientAsync", 1L);
        _backend.Urls.ShouldBeEmpty();
        await dialogs.Received(1).ShowMessageBox("Msg_ConfirmDeleteTitle", "Common_DeleteConfirm", "Btn_Delete", null, "Btn_Cancel", null);
    }

    [Fact]
    public async Task SavingClientName_SendsAdditionalContactsAndPreservesInactiveStatus()
    {
        var page = new ClientDetail();
        Set(page, "Id", 1L);
        Set(page, "L", _localizer);
        Set(page, "Snackbar", Services.GetRequiredService<ISnackbar>());
        Set(page, "ClientService", Services.GetRequiredService<ClientApiService>());
        Set(page, "_client", new ClientDto
        {
            Id = 1, IsActive = false,
            Contact = [new ContactDto { ContactType = EContactType.Email, ContactValue = "first@example.test", Label = "Billing" },
                       new ContactDto { ContactType = EContactType.Email, ContactValue = "second@example.test", Label = "Owner", IsPrimary = true }]
        });
        Set(page, "_editClient", new CreateClientDto { CompanyName = "New name" });
        Set(page, "_emailContact", "first@example.test");
        await Invoke(page, "SaveClient");
        var request = JsonSerializer.Deserialize<UpdateClientDto>(_backend.LastWriteBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        request.CompanyName.ShouldBe("New name");
        request.IsActive.ShouldBe(false);
        request.Contact!.Count.ShouldBe(2);
        request.Contact[1].ContactValue.ShouldBe("second@example.test");
        request.Contact[1].Label.ShouldBe("Owner");
        request.Contact[1].IsPrimary.ShouldBe(true);
    }

    private static Task Invoke(object target, string method, params object?[] args) =>
        (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

    private static void Set(object target, string name, object value)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var field = target.GetType().GetField(name, flags);
        if (field is not null) field.SetValue(target, value);
        else target.GetType().GetProperty(name, flags)!.SetValue(target, value);
    }

    private sealed class Backend : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public bool FailPayments, FailInvoices, HoldWrites;
        public int Writes;
        public string? LastWriteBody;
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Urls.Add(uri.PathAndQuery);
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put)
            {
                Writes++;
                LastWriteBody = await request.Content!.ReadAsStringAsync(ct);
                WriteStarted.TrySetResult();
                if (HoldWrites) await ReleaseWrite.Task.WaitAsync(ct);
                return Json("null"); // No navigation: exercise guard reset on an unsuccessful save.
            }
            if (uri.AbsolutePath.Contains("transactions"))
            {
                if (FailPayments) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Unavailable") };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Fakvio.Contracts.Common.Pagination.PagedResult<BankTransactionDto>([new BankTransactionDto { Id = 1, Amount = 10 }], 150, 1, uri.Query.Contains("pageSize=25") ? 25 : 50))
                };
            }
            if (uri.AbsolutePath == "/api/client/1")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ClientDto { Id = 1, CompanyName = "Customer", Language = "cs" }) };
            if (uri.AbsolutePath == "/api/client/issuer") return Json("null");
            if (uri.AbsolutePath == "/api/invoice/paged")
            {
                if (FailInvoices) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Unavailable") };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new Fakvio.Contracts.Common.Pagination.PagedResult<InvoiceDto>([new InvoiceDto { Id = 19, DocumentNumber = "old-2019" }], 1, 1, 50))
                };
            }
            return Json("[]");
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
