// ============================================================================
// IntegrationsPageTests — bUnit coverage for /settings/integrations (issue #237).
//
// ApiKeyServiceTests covers the server side. This file covers the part the user
// actually sees, and specifically the one property that cannot be recovered if it
// is wrong: the raw API key is shown EXACTLY ONCE, next to an explicit warning,
// and never appears in the key list.
//
// The page is rendered over a real ApiKeyApiService wired to a stubbed HTTP handler
// and next to a real MudDialogProvider, so create and revoke are exercised through
// the same dialogs the user sees — response body, confirmation and all.
// ============================================================================

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Contracts.Dto.User;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the personal API keys page.
/// </summary>
public class IntegrationsPageTests : BunitContext, IAsyncLifetime
{
    /// <summary>The raw key the stubbed API hands back from POST /api/api-key.</summary>
    private const string RawKey = "fak_live_TESTKEY0123456789abcdefghijklmnopqrstuvw";

    private const string ActiveKeyName = "Claude Desktop";
    private const string ActiveKeyPrefix = "fak_live_ABC";

    /// <summary>Name the tests type into the create dialog.</summary>
    private const string NewKeyName = "Claude Code";

    // The two scope values the dialog offers. Copies of the page's own private
    // constants on purpose — a test that computed them from the page could not
    // notice the page sending the wrong one.
    private const string ScopeRead = "read";
    private const string ScopeReadWrite = "read,write";

    /// <summary>
    /// Base address of the API the page talks to. Deliberately not "localhost" and not the
    /// address bUnit navigates from: the two are different hosts in every real deployment,
    /// which is the whole point of the snippet assertion below.
    /// </summary>
    private const string ApiBaseUrl = "https://api.test.local";

    private readonly ApiKeyStub _api = new(RawKey);

    // MudBlazor's PopoverService only supports async disposal; xunit v2 disposes
    // test classes synchronously, so route disposal through IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public IntegrationsPageTests()
    {
        // The create dialog holds a MudSelect and a MudDatePicker, which refuse to
        // initialise without a MudPopoverProvider in a bUnit render tree (same reason as
        // MyCompanyEpoSectionTests) — switch the guard off instead of faking a layout.
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();

        // Real localization (same wiring as AddSharedUiServices) instead of a substitute:
        // it also proves the new resource keys exist — a missing key would render its own
        // name and the message assertions below would fail.
        Services.AddLocalization(options => options.ResourcesPath = "Resources");

        // Real API client over the stub, so DTO serialization is part of the test.
        var httpClient = new HttpClient(_api) { BaseAddress = new Uri(ApiBaseUrl) };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);
        Services.AddSingleton(Substitute.For<AuthenticationStateProvider>());
        Services.AddSingleton<ApiKeyApiService>();

        // The page reports failures through the shared handler; a substitute keeps the
        // test output clean without hiding anything the assertions look at.
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

    /// <summary>Resolves a resource key exactly the way the page does.</summary>
    private string Localized(string key)
    {
        var localized = Services.GetRequiredService<IStringLocalizer<SharedResource>>()[key];
        localized.ResourceNotFound.ShouldBeFalse($"resource key '{key}' is missing from SharedResource.resx");
        return localized.Value;
    }

    /// <summary>
    /// Renders the page with the stub already holding the given keys.
    /// </summary>
    private IRenderedComponent<PageHost> RenderPageWithKeys(params ApiKeyDto[] keys)
    {
        _api.Keys.Clear();
        _api.Keys.AddRange(keys);

        return Render<PageHost>();
    }

    /// <summary>
    /// The page plus a MudDialogProvider. Both the create dialog and the revoke
    /// confirmation are rendered by the provider rather than inline, so without it
    /// neither would ever appear — and asserting on one root component keeps the
    /// page markup and the dialog markup in the same query scope.
    /// </summary>
    public sealed class PageHost : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Integrations>(1);
            builder.CloseComponent();
        }
    }

    private static ApiKeyDto ActiveKey() => new()
    {
        Id = 1,
        Name = ActiveKeyName,
        KeyPrefix = ActiveKeyPrefix,
        Scopes = ScopeRead,
        CreatedAt = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc)
    };

    /// <summary>Clicks the button whose title attribute matches the given label.</summary>
    private static void ClickButtonTitled(IRenderedComponent<PageHost> page, string title)
    {
        var button = page.FindAll("button").FirstOrDefault(b => b.GetAttribute("title") == title);
        button.ShouldNotBeNull($"no button titled '{title}' is rendered");
        button.Click();
    }

    /// <summary>Clicks the dialog button whose caption contains the given text.</summary>
    private static void ClickDialogButton(IRenderedComponent<PageHost> page, string caption)
    {
        var buttons = page.FindAll(".mud-dialog button");
        var button = buttons.FirstOrDefault(b => b.TextContent.Contains(caption));
        button.ShouldNotBeNull(
            $"no dialog button captioned '{caption}'; rendered: " +
            string.Join(" | ", buttons.Select(b => $"[{b.TextContent.Trim()}]")));
        button.Click();
    }

    /// <summary>
    /// Opens the create dialog, fills the form in and submits it. <paramref name="scopes"/>
    /// and <paramref name="expiresOn"/> stay untouched when null, which is what the user
    /// sees when they only type a name and hit Create.
    /// </summary>
    private async Task CreateKeyNamed(
        IRenderedComponent<PageHost> page,
        string name,
        string? scopes = null,
        DateTime? expiresOn = null)
    {
        ClickButtonTitled(page, Localized("Integration_NewKey"));

        // Scoped to the dialog: the grid's column filter row renders inputs too.
        var nameInput = page.FindAll(".mud-dialog input").FirstOrDefault();
        nameInput.ShouldNotBeNull("the create dialog must render a name field");
        nameInput.Input(name);

        // MudSelect and MudDatePicker open their values in a popover, which a bUnit
        // render tree does not lay out; invoking the two-way binding callback is the
        // same write the popover would perform and keeps the page as the component
        // under test rather than MudBlazor.
        if (scopes is not null)
        {
            var select = page.FindComponent<MudSelect<string>>();
            await page.InvokeAsync(() => select.Instance.ValueChanged.InvokeAsync(scopes));
        }

        if (expiresOn is not null)
        {
            var datePicker = page.FindComponent<MudDatePicker>();
            await page.InvokeAsync(() => datePicker.Instance.DateChanged.InvokeAsync(expiresOn));
        }

        // MudForm.IsValid — which gates the confirm button — only turns true once every
        // registered field has been validated; a browser does that as the user tabs
        // through the form, a bUnit render tree needs one explicit pass.
        var form = page.FindComponent<MudForm>();
        await page.InvokeAsync(() => form.Instance.Validate());

        ClickDialogButton(page, Localized("Btn_Create"));

        // Precondition, not the assertion under test: if the dialog silently stopped
        // submitting, every reveal test below would "pass" by finding nothing.
        // WaitForAssertion because the create call and the re-render that follows it are
        // asynchronous — the click returns before the response has been handled.
        page.WaitForAssertion(() =>
            _api.CreateCount.ShouldBe(1, "the create dialog did not reach the API"));
    }

    // ── List ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The list is built from data that contains no credential at all, and it has to
    /// stay that way: prefix in, key out.
    /// </summary>
    [Fact]
    public void Integrations_ListsKeys_ShowingPrefixNotKey()
    {
        var page = RenderPageWithKeys(ActiveKey());

        page.Markup.ShouldContain(ActiveKeyName);
        page.Markup.ShouldContain(ActiveKeyPrefix);
        page.Markup.ShouldNotContain(RawKey);
    }

    /// <summary>An empty list explains itself instead of showing a bare grid.</summary>
    [Fact]
    public void Integrations_WithoutKeys_ShowsEmptyHint()
    {
        var page = RenderPageWithKeys();

        page.Markup.ShouldContain(Localized("Integration_NoKeys"));
    }

    /// <summary>A revoked key is labelled as such and loses its revoke button.</summary>
    [Fact]
    public void Integrations_RevokedKey_ShowsStatusAndNoRevokeButton()
    {
        var revoked = ActiveKey();
        revoked.RevokedAt = new DateTime(2026, 8, 2, 10, 0, 0, DateTimeKind.Utc);

        var page = RenderPageWithKeys(revoked);

        page.Markup.ShouldContain(Localized("Integration_StatusRevoked"));
        page.FindAll("button")
            .Any(b => b.GetAttribute("title") == Localized("Integration_Revoke"))
            .ShouldBeFalse("a revoked key cannot be revoked again");
    }

    // ── What the create dialog sends ──────────────────────────────────────

    /// <summary>
    /// Typing just a name has to produce the safe defaults the dialog displays:
    /// read-only and no expiry. Anything else would silently hand out more access
    /// (or less lifetime) than the form promised.
    /// </summary>
    [Fact]
    public async Task Integrations_Create_SendsTypedNameWithTheDefaultsShownInTheDialog()
    {
        var page = RenderPageWithKeys();

        await CreateKeyNamed(page, NewKeyName);

        var request = _api.LastCreateRequest.ShouldNotBeNull();
        request.Name.ShouldBe(NewKeyName);
        request.Scopes.ShouldBe(ScopeRead);
        request.ExpiresAt.ShouldBeNull("an untouched date picker means the key never expires");
    }

    /// <summary>
    /// Scope is one of the three fields the user controls, so the chosen one — not the
    /// default — has to reach the API. A key quietly downgraded to read-only fails every
    /// write the user connected their AI client for, and cannot be upgraded afterwards.
    /// </summary>
    [Fact]
    public async Task Integrations_Create_SendsTheSelectedScope()
    {
        var page = RenderPageWithKeys();

        await CreateKeyNamed(page, NewKeyName, scopes: ScopeReadWrite);

        _api.LastCreateRequest.ShouldNotBeNull().Scopes.ShouldBe(ScopeReadWrite);
    }

    /// <summary>
    /// The picker offers a day, not an instant, and the key must stay valid for the whole
    /// of that day — so the expiry sent is midnight at the start of the FOLLOWING day,
    /// converted from the user's zone to UTC. Sending the chosen day's own midnight would
    /// expire the key before its last day ever started.
    /// </summary>
    [Fact]
    public async Task Integrations_Create_SendsExpiryAtMidnightAfterTheChosenDay()
    {
        var page = RenderPageWithKeys();

        // Month boundary on purpose — that is where naive date arithmetic breaks.
        await CreateKeyNamed(page, NewKeyName, expiresOn: new DateTime(2026, 9, 30));

        var startOfNextDayUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified)
            .ToUniversalTime();
        _api.LastCreateRequest.ShouldNotBeNull().ExpiresAt.ShouldBe(startOfNextDayUtc);
    }

    // ── One-time reveal ───────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the page: after creating a key the raw value is on screen,
    /// with the "you will not see this again" warning next to it.
    /// </summary>
    [Fact]
    public async Task Integrations_AfterCreate_RevealsRawKeyWithWarning()
    {
        var page = RenderPageWithKeys();

        await CreateKeyNamed(page, NewKeyName);

        page.WaitForAssertion(() =>
        {
            page.Markup.ShouldContain(RawKey);
            page.Markup.ShouldContain(Localized("Integration_RawKeyWarning"));
        });
    }

    /// <summary>
    /// "Once" means once: dismissing the panel removes the key from the page, and the
    /// refreshed list (which now holds the new key's metadata) must not bring it back.
    /// </summary>
    [Fact]
    public async Task Integrations_AfterDismissingReveal_RawKeyIsGone()
    {
        var page = RenderPageWithKeys();
        await CreateKeyNamed(page, NewKeyName);

        page.WaitForAssertion(() => page.Markup.ShouldContain(RawKey));

        page.FindAll("button")
            .First(b => b.TextContent.Contains(Localized("Integration_KeySaved")))
            .Click();

        page.Markup.ShouldNotContain(RawKey);
    }

    /// <summary>
    /// Both config snippets are offered, each already carrying the key — that is what
    /// makes the onboarding copy-paste. The stdio one configures the local tool, the
    /// HTTP one sends the key as a bearer token.
    /// </summary>
    [Fact]
    public async Task Integrations_AfterCreate_OffersStdioAndHttpSnippetsContainingTheKey()
    {
        var page = RenderPageWithKeys();

        await CreateKeyNamed(page, NewKeyName);

        page.WaitForAssertion(() =>
        {
            page.Markup.ShouldContain(Localized("Integration_SnippetStdio"));
            page.Markup.ShouldContain(Localized("Integration_SnippetHttp"));
            page.Markup.ShouldContain($"\"FAKVIO_API_TOKEN\": \"{RawKey}\"");
            page.Markup.ShouldContain($"Bearer {RawKey}");
        });
    }

    /// <summary>
    /// The stdio snippet must carry the address of the **API**, not the address the user has
    /// the app open at in the browser.
    ///
    /// This is USERGUIDE §20.3's escape hatch: the guide tells a user who does not know the
    /// value to copy it out of this block. A snippet built from the host address instead would
    /// start `fakvio-mcp` fine and fail every call on connection — the exact defect review
    /// round 1 of #242 found in the prose, one layer down in the code that generates the value.
    /// </summary>
    [Fact]
    public async Task Integrations_StdioSnippet_CarriesTheApiBaseUrl()
    {
        var page = RenderPageWithKeys();

        await CreateKeyNamed(page, NewKeyName);

        page.WaitForAssertion(() =>
            page.Markup.ShouldContain($"\"FAKVIO_API_URL\": \"{ApiBaseUrl}\""));
    }

    // ── Revoke ────────────────────────────────────────────────────────────

    /// <summary>
    /// Revoking is destructive and irreversible, so it must ask first — and must not
    /// touch the API while the question is still on screen.
    /// </summary>
    [Fact]
    public void Integrations_Revoke_AsksForConfirmationBeforeCallingApi()
    {
        var page = RenderPageWithKeys(ActiveKey());

        ClickButtonTitled(page, Localized("Integration_Revoke"));

        page.Markup.ShouldContain(Localized("Integration_RevokeConfirmTitle"));
        _api.RevokedIds.ShouldBeEmpty("the key must survive until the user confirms");
    }

    /// <summary>Cancelling the confirmation leaves the key alone.</summary>
    [Fact]
    public void Integrations_Revoke_Cancelled_DoesNotCallApi()
    {
        var page = RenderPageWithKeys(ActiveKey());

        ClickButtonTitled(page, Localized("Integration_Revoke"));
        ClickDialogButton(page, Localized("Btn_Cancel"));

        _api.RevokedIds.ShouldBeEmpty();
    }

    /// <summary>
    /// Confirming actually revokes that key on the server — on the one route the API
    /// serves (see <c>ApiKeyStub.RevokeRoute</c>).
    /// </summary>
    [Fact]
    public void Integrations_Revoke_Confirmed_CallsApi()
    {
        var page = RenderPageWithKeys(ActiveKey());

        ClickButtonTitled(page, Localized("Integration_Revoke"));
        ClickDialogButton(page, Localized("Integration_Revoke"));

        _api.RevokedIds.ShouldBe([1L]);
    }

    /// <summary>
    /// A confirmed revoke has to tell the user it worked. This is the page's only
    /// feedback channel for it: the client turns 404 into "already gone" on purpose,
    /// so a revoke that never lands anywhere leaves the user with no success message,
    /// no error, and a key that stays alive.
    /// </summary>
    [Fact]
    public void Integrations_Revoke_Confirmed_ShowsSuccessMessage()
    {
        var page = RenderPageWithKeys(ActiveKey());

        ClickButtonTitled(page, Localized("Integration_Revoke"));
        ClickDialogButton(page, Localized("Integration_Revoke"));

        page.WaitForAssertion(() =>
        {
            var snackbars = Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();
            snackbars.Count.ShouldBe(1);
            snackbars[0].Severity.ShouldBe(Severity.Success);
        });
    }

    /// <summary>
    /// Stubbed API-key endpoints: list, create (returns the raw key once) and revoke.
    /// Created keys are appended to the list so the reload after create sees them.
    ///
    /// The stub is deliberately strict about both the request body and the route: it
    /// deserializes what the page posted (so scope and expiry are observable, not just
    /// the name) and answers revoke only on the exact documented path. A looser route
    /// match would hide a wrong URL completely — the page swallows 404 from revoke by
    /// design, so a typo'd route reaches the user as "nothing happened, no error".
    /// </summary>
    private sealed class ApiKeyStub : HttpMessageHandler
    {
        /// <summary>The one route that revokes; must match <c>ApiKeyApiService.RevokeAsync</c>.</summary>
        private static readonly Regex RevokeRoute =
            new(@"^/api/api-key/(?<id>\d+)/revoke$", RegexOptions.Compiled);

        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly string _rawKey;

        public ApiKeyStub(string rawKey) => _rawKey = rawKey;

        public List<ApiKeyDto> Keys { get; } = [];

        public List<long> RevokedIds { get; } = [];

        public int CreateCount { get; private set; }

        /// <summary>Body of the last POST /api/api-key, exactly as the page serialized it.</summary>
        public CreateApiKeyDto? LastCreateRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/api/api-key")
                return Json(HttpStatusCode.OK, Keys);

            if (request.Method == HttpMethod.Post && path == "/api/api-key")
                return await CreateKeyAsync(request, cancellationToken);

            var revoke = RevokeRoute.Match(path);
            if (request.Method == HttpMethod.Post && revoke.Success)
            {
                RevokedIds.Add(long.Parse(revoke.Groups["id"].Value));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        /// <summary>
        /// Echoes the requested name, scope and expiry back the way the real controller
        /// does, so the reloaded list reflects what the user actually asked for.
        /// </summary>
        private async Task<HttpResponseMessage> CreateKeyAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CreateCount++;
            LastCreateRequest = await request.Content!
                .ReadFromJsonAsync<CreateApiKeyDto>(JsonOptions, cancellationToken);

            var created = new CreatedApiKeyDto
            {
                Id = Keys.Count + 1,
                Name = LastCreateRequest!.Name,
                KeyPrefix = _rawKey[..12],
                Scopes = LastCreateRequest.Scopes,
                ExpiresAt = LastCreateRequest.ExpiresAt,
                CreatedAt = DateTime.UtcNow,
                Key = _rawKey
            };

            // The list endpoint never returns the raw key — only the metadata.
            Keys.Add(new ApiKeyDto
            {
                Id = created.Id,
                Name = created.Name,
                KeyPrefix = created.KeyPrefix,
                Scopes = created.Scopes,
                ExpiresAt = created.ExpiresAt,
                CreatedAt = created.CreatedAt
            });

            return Json(HttpStatusCode.Created, created);
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
    }
}
