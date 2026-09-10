// ============================================================================
// InvoicesTypeFilterTests — bUnit coverage for /invoices?type=… (issue #376).
//
// /invoices, /invoices?type=Proforma and /invoices?type=CreditNote all share the
// same @page route, so navigating between them does NOT create a new component
// instance. The document-type filter used to be resolved in OnInitializedAsync,
// which runs once per instance, so the grid kept showing the previously selected
// type until the user pressed F5.
//
// The regression test drives the real mechanism — a navigation that changes only
// the query string — rather than pushing the parameter in by hand, because it is
// exactly that path which was broken. Assertions look at the URLs the page asked
// the API for, since that is where the stale filter actually leaked out.
// ============================================================================

using System.Net;
using System.Text;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.Contracts.Dto.User;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the document-type filter on the invoice list page.
/// </summary>
public class InvoicesTypeFilterTests : BunitContext, IAsyncLifetime
{
    /// <summary>Base address of the API the page talks to.</summary>
    private const string ApiBaseUrl = "https://api.test.invalid";

    /// <summary>Records every request the page makes and answers them all as "empty".</summary>
    private readonly RecordingApiStub _api = new();

    // xUnit disposes test classes synchronously; route disposal through IAsyncLifetime
    // (same reason as IntegrationsPageTests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public InvoicesTypeFilterTests()
    {
        // The page's toolbar holds MudSelects, which refuse to initialise without a
        // MudPopoverProvider in a bUnit render tree — switch the guard off instead of
        // faking a layout (same reason as IntegrationsPageTests).
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(options => options.ResourcesPath = "Resources");

        // This is what makes [SupplyParameterFromQuery] work outside the Router: the
        // framework supplies those values through a cascading provider that reads the
        // NavigationManager's URI. Without it the page would never see ?type= at all,
        // and the test would prove nothing about the real navigation path.
        Services.AddSupplyValueFromQueryProvider();

        // Real API clients over the stub, so the query string the page builds is the
        // one under assertion.
        var httpClient = new HttpClient(_api) { BaseAddress = new Uri(ApiBaseUrl) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);
        Services.AddSingleton(Substitute.For<AuthenticationStateProvider>());
        Services.AddSingleton<FakvioService>();
        Services.AddSingleton<ClientApiService>();
        Services.AddSingleton<InvoiceTemplateApiService>();
        Services.AddSingleton<ContentTemplateApiService>();
        Services.AddSingleton<AlertApiService>();
        Services.AddSingleton(Substitute.For<IUiErrorHandler>());

        // FakvioGrid dependencies: it awaits the user's page-size preference before
        // rendering and persists column state through local storage.
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton<GridStateService>();
        var preferencesState = Substitute.For<UserPreferencesState>();
        preferencesState.Preferences.Returns(new UserPreferencesDto());
        preferencesState.EnsureLoadedAsync().Returns(new UserPreferencesDto());
        Services.AddSingleton(preferencesState);

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Navigates to the given URL and renders the page, exactly as entering the URL would.
    /// </summary>
    private IRenderedComponent<Invoices> RenderAt(string url)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<Invoices>();
    }

    /// <summary>
    /// The DocumentType values the page asked the paged endpoint for, oldest first.
    /// A view that never queried the API contributes nothing, which is itself a failure
    /// the assertions below can see.
    /// </summary>
    private IReadOnlyList<string> RequestedDocumentTypes() =>
        _api.RequestedUris
            .Where(u => u.AbsolutePath.Contains("/api/invoice/paged", StringComparison.Ordinal))
            .Select(u => System.Web.HttpUtility.ParseQueryString(u.Query)["DocumentType"])
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToList();

    [Fact]
    public void Initial_render_filters_by_the_type_from_the_query_string()
    {
        RenderAt($"{ApiBaseUrl}/invoices?type=Proforma");

        // OnParametersSetAsync also runs before the first render, so moving the mapping
        // there must not break the plain "open the URL" case.
        RequestedDocumentTypes().ShouldContain("Proforma");
    }

    [Fact]
    public void Initial_render_without_a_type_falls_back_to_plain_invoices()
    {
        RenderAt($"{ApiBaseUrl}/invoices");

        // No param means standard invoices only — proformas and credit notes stay out.
        RequestedDocumentTypes().ShouldContain("Invoice");
    }

    [Fact]
    public void Changing_the_type_query_parameter_refetches_without_a_remount()
    {
        var cut = RenderAt($"{ApiBaseUrl}/invoices?type=Proforma");
        var instanceBefore = cut.Instance;

        // The regression: same route, only the query string differs, so Blazor keeps the
        // component alive and only re-runs the parameter lifecycle.
        cut.InvokeAsync(() =>
            Services.GetRequiredService<NavigationManager>()
                    .NavigateTo($"{ApiBaseUrl}/invoices?type=CreditNote"));

        cut.Instance.ShouldBeSameAs(instanceBefore,
            "the test is only meaningful while the component is NOT recreated");

        RequestedDocumentTypes().ShouldContain("CreditNote",
            "the grid must refetch with the new type instead of waiting for an F5");
    }

    /// <summary>
    /// Answers every request with an empty payload and remembers what was asked for.
    /// The page's loaders swallow their own failures, so an unrecognised path would
    /// silently skew the test — everything gets a valid empty body instead.
    /// </summary>
    private sealed class RecordingApiStub : HttpMessageHandler
    {
        private readonly List<Uri> _requestedUris = [];

        public IReadOnlyList<Uri> RequestedUris
        {
            get { lock (_requestedUris) return _requestedUris.ToList(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is not null)
                lock (_requestedUris) _requestedUris.Add(request.RequestUri);

            // Paged endpoints return an envelope, everything else a bare collection.
            var body = request.RequestUri?.AbsolutePath.Contains("/paged", StringComparison.Ordinal) == true
                ? """{"items":[],"totalCount":0,"page":1,"pageSize":50,"totalPages":0}"""
                : "[]";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
