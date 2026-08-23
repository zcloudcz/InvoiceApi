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
using System.Text;
using System.Text.Json;
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
        var httpClient = new HttpClient(_api) { BaseAddress = new Uri("https://api.test.local") };
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
        Scopes = "read",
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

    /// <summary>Opens the create dialog, fills in a name and submits it.</summary>
    private async Task CreateKeyNamed(IRenderedComponent<PageHost> page, string name)
    {
        ClickButtonTitled(page, Localized("Integration_NewKey"));

        // Scoped to the dialog: the grid's column filter row renders inputs too.
        var nameInput = page.FindAll(".mud-dialog input").FirstOrDefault();
        nameInput.ShouldNotBeNull("the create dialog must render a name field");
        nameInput.Input(name);

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

    // ── One-time reveal ───────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the page: after creating a key the raw value is on screen,
    /// with the "you will not see this again" warning next to it.
    /// </summary>
    [Fact]
    public async Task Integrations_AfterCreate_RevealsRawKeyWithWarning()
    {
        var page = RenderPageWithKeys();

        await CreateKeyNamed(page, "Claude Code");

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
        await CreateKeyNamed(page, "Claude Code");

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

        await CreateKeyNamed(page, "Claude Code");

        page.WaitForAssertion(() =>
        {
            page.Markup.ShouldContain(Localized("Integration_SnippetStdio"));
            page.Markup.ShouldContain(Localized("Integration_SnippetHttp"));
            page.Markup.ShouldContain($"\"FAKVIO_API_TOKEN\": \"{RawKey}\"");
            page.Markup.ShouldContain($"Bearer {RawKey}");
        });
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

    /// <summary>Confirming actually revokes that key on the server.</summary>
    [Fact]
    public void Integrations_Revoke_Confirmed_CallsApi()
    {
        var page = RenderPageWithKeys(ActiveKey());

        ClickButtonTitled(page, Localized("Integration_Revoke"));
        ClickDialogButton(page, Localized("Integration_Revoke"));

        _api.RevokedIds.ShouldBe([1L]);
    }

    /// <summary>
    /// Stubbed API-key endpoints: list, create (returns the raw key once) and revoke.
    /// Created keys are appended to the list so the reload after create sees them.
    /// </summary>
    private sealed class ApiKeyStub : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web);

        private readonly string _rawKey;

        public ApiKeyStub(string rawKey) => _rawKey = rawKey;

        public List<ApiKeyDto> Keys { get; } = [];

        public List<long> RevokedIds { get; } = [];

        public int CreateCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path == "/api/api-key")
                return Task.FromResult(Json(HttpStatusCode.OK, Keys));

            if (request.Method == HttpMethod.Post && path == "/api/api-key")
            {
                CreateCount++;
                var created = new CreatedApiKeyDto
                {
                    Id = Keys.Count + 1,
                    Name = "created",
                    KeyPrefix = _rawKey[..12],
                    Scopes = "read",
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
                    CreatedAt = created.CreatedAt
                });

                return Task.FromResult(Json(HttpStatusCode.Created, created));
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/revoke"))
            {
                RevokedIds.Add(long.Parse(path.Split('/')[^2]));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) => new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
    }
}
