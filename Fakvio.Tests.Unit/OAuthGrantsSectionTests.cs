using System.Net;
using System.Net.Http.Json;
using Blazored.LocalStorage;
using Bunit;
using Fakvio.Contracts.Dto.ApiKey;
using Fakvio.Contracts.Dto.OAuth;
using Fakvio.Contracts.Dto.User;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit coverage for the "Připojené aplikace" section on /settings/integrations (ADR 0001,
/// docs/adr/0001-mcp-oauth21.md §4.8, task N5.7). <see cref="IntegrationsPageTests"/> covers
/// the API-keys half of the same page; this covers the OAuth-grants half added alongside it.
/// </summary>
public class OAuthGrantsSectionTests : BunitContext, IAsyncLifetime
{
    private readonly GrantsStub _api = new();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public OAuthGrantsSectionTests()
    {
        Services.AddSingleton<IConfiguration>(_ => new ConfigurationBuilder().Build());
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var httpClient = new HttpClient(_api) { BaseAddress = new Uri("https://api.test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);
        Services.AddSingleton(Substitute.For<AuthenticationStateProvider>());
        Services.AddSingleton<ApiKeyApiService>();
        Services.AddSingleton<CompanyMembershipApiService>();
        Services.AddSingleton<OAuthGrantsApiService>();
        Services.AddSingleton(Substitute.For<IUiErrorHandler>());
        Services.AddSingleton(Substitute.For<ILocalStorageService>());
        Services.AddSingleton<GridStateService>();
        var preferencesState = Substitute.For<UserPreferencesState>();
        preferencesState.Preferences.Returns(new UserPreferencesDto());
        preferencesState.EnsureLoadedAsync().Returns(new UserPreferencesDto());
        Services.AddSingleton(preferencesState);

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class PageHost : Microsoft.AspNetCore.Components.ComponentBase
    {
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudDialogProvider>(0);
            builder.CloseComponent();
            builder.OpenComponent<Integrations>(1);
            builder.CloseComponent();
        }
    }

    private static OAuthGrantDto Grant() => new()
    {
        Id = 1, ClientId = "https://claude.ai/oauth/claude-code-client-metadata",
        ClientName = "Claude Code", Scopes = "read", CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void NoGrants_SectionIsNotShown()
    {
        _api.Grants.Clear();

        var page = Render<PageHost>();

        page.WaitForAssertion(() => page.Markup.ShouldNotBeNullOrEmpty());
        page.Markup.ShouldNotContain("claude.ai");
    }

    [Fact]
    public void OneGrant_ShowsItsClientIdAndName()
    {
        _api.Grants.Add(Grant());

        var page = Render<PageHost>();

        page.WaitForAssertion(() => page.Markup.ShouldContain("https://claude.ai/oauth/claude-code-client-metadata"));
        page.Markup.ShouldContain("Claude Code");
    }

    [Fact]
    public void Remove_AsksForConfirmation_ThenCallsRevokeAndReloads()
    {
        _api.Grants.Add(Grant());

        var page = Render<PageHost>();
        page.WaitForAssertion(() => page.Markup.ShouldContain("Claude Code"));

        var removeButton = page.FindAll("button").First(b => b.GetAttribute("title") == "Odebrat");
        removeButton.Click();

        // MudMessageBox is rendered by the MudDialogProvider — confirm it.
        page.WaitForAssertion(() => page.FindAll(".mud-dialog-container").Count.ShouldBe(1));
        var confirmButton = page.FindAll(".mud-dialog-actions button").Last();
        confirmButton.Click();

        page.WaitForAssertion(() => _api.RevokedIds.ShouldContain(1L));
        page.WaitForAssertion(() => page.Markup.ShouldNotContain("Claude Code"));
    }

    private sealed class GrantsStub : HttpMessageHandler
    {
        public List<OAuthGrantDto> Grants { get; } = [];
        public List<long> RevokedIds { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/api/my-companies") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new Fakvio.Contracts.Dto.CompanyMembership.CompanyMembershipDto { CompanyId = 1, CompanyName = "Company one", IsDefault = true, IsProvisioned = true } }) });

            if (request.Method == HttpMethod.Get && path == "/api/api-key")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new List<ApiKeyDto>())
                });

            if (request.Method == HttpMethod.Get && path == "/api/oauth/grants")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Grants)
                });

            if (request.Method == HttpMethod.Post && path.StartsWith("/api/oauth/grants/") && path.EndsWith("/revoke"))
            {
                var id = long.Parse(path.Split('/')[^2]);
                RevokedIds.Add(id);
                Grants.RemoveAll(g => g.Id == id);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
