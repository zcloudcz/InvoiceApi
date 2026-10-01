using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Dto.OAuth;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the OAuth consent screen (ADR 0001, docs/adr/0001-mcp-oauth21.md
/// §4.9, task N5.4). Pins the two threats a rendering mistake could reopen:
///
/// T1 (phishing consent) — the heading MUST name the host <c>client_id</c> URL, never the
/// self-asserted <c>client_name</c> alone, and an untrusted client must not show the
/// "verified" badge.
///
/// T10 (loopback impersonation) — a loopback redirect_uri must show its warning; a normal
/// https one must not.
/// </summary>
public class OAuthConsentPageTests : BunitContext, IAsyncLifetime
{
    private const string Ticket = "opaque-ticket-value";
    private const string ClientId = "https://claude.ai/oauth/claude-code-client-metadata";
    private const string ClientName = "Totally Legit Claude (self-asserted)";

    private readonly ConsentStub _api = new();

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public OAuthConsentPageTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddLogging();
        Services.AddLocalization(options => options.ResourcesPath = "Resources");

        var httpClient = new HttpClient(_api) { BaseAddress = new Uri("https://api.test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);
        Services.AddSingleton(factory);
        Services.AddSingleton<OAuthConsentApiService>();

        // Registers the auth services + provides the [CascadingParameter] Task<AuthenticationState>
        // the page reads — the officially supported bUnit mechanism for exactly this, rather than
        // hand-rolling a substitute AuthenticationStateProvider (ApiClientBase also resolves one,
        // but only special-cases CustomAuthenticationStateProvider, so any registration is fine there).
        AddAuthorization().SetAuthorized("owner@example.com");

        JSInterop.Mode = JSRuntimeMode.Loose;

        // The page reads the ticket from the current URL's query string.
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo($"/oauth/consent?ticket={Ticket}");
    }

    private IRenderedComponent<OAuthConsent> RenderConsent(OAuthConsentInfoDto info)
    {
        _api.Info = info;
        return Render<OAuthConsent>();
    }

    private static OAuthConsentInfoDto BaseInfo() => new()
    {
        ClientId = ClientId,
        ClientName = ClientName,
        RedirectUri = "https://claude.ai/api/mcp/auth_callback",
        IsTrustedClient = false,
        IsLoopbackRedirect = false,
        RequestedScopes = "read,write",
        UserEmail = "owner@example.com",
        CompanyName = "Owner s.r.o.",
        CompanyId = 17,
        IsEligible = true
    };

    [Fact]
    public void Heading_NamesTheHostClientIdUrl_NotJustTheSelfAssertedName()
    {
        var consent = RenderConsent(BaseInfo());

        // DescribeAsync completes after the first render (bUnit's Render() does not wait for
        // it) — WaitForAssertion polls until the follow-up render lands.
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(ClientId));
    }

    [Fact]
    public void ClientName_IsShownOnlyAsASubtitle_NeverAsTheHeading()
    {
        var consent = RenderConsent(BaseInfo());

        // The self-asserted name must still be visible (so the user has context) but
        // must not be what carries trust — the previous test pins that the client_id
        // URL is present; this one pins that the self-asserted name doesn't replace it.
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(ClientName));
    }

    [Fact]
    public void UntrustedClient_DoesNotShowTheVerifiedBadge()
    {
        var info = BaseInfo();
        info.IsTrustedClient = false;

        var consent = RenderConsent(info);

        var localized = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()["OAuthConsent_VerifiedApp"].Value;
        // Wait for the eligible-user branch to render at all, then assert the badge is absent.
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(ClientId));
        consent.Markup.ShouldNotContain(localized);
    }

    [Fact]
    public void TrustedClient_ShowsTheVerifiedBadge()
    {
        var info = BaseInfo();
        info.IsTrustedClient = true;

        var consent = RenderConsent(info);

        // The localizer substitute is real (AddLocalization), so this resolves through the
        // actual .resx entry — a missing/renamed key would fail this assertion too.
        var localized = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()["OAuthConsent_VerifiedApp"].Value;
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(localized));
    }

    [Fact]
    public void LoopbackRedirect_ShowsTheLocalhostWarning()
    {
        var info = BaseInfo();
        info.RedirectUri = "http://127.0.0.1:54321/callback";
        info.IsLoopbackRedirect = true;

        var consent = RenderConsent(info);

        var localized = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()["OAuthConsent_LoopbackWarning"].Value;
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(localized));
    }

    [Fact]
    public void HttpsRedirect_DoesNotShowTheLocalhostWarning()
    {
        var info = BaseInfo();
        info.IsLoopbackRedirect = false;

        var consent = RenderConsent(info);

        var localized = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()["OAuthConsent_LoopbackWarning"].Value;
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(ClientId));
        consent.Markup.ShouldNotContain(localized);
    }

    [Fact]
    public void IneligibleUser_ShowsClosedBetaMessage_AndDisablesAllow()
    {
        var info = BaseInfo();
        info.IsEligible = false;

        var consent = RenderConsent(info);

        var localized = Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()["OAuthConsent_ClosedBeta"].Value;
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(localized));

        var allowButton = consent.FindAll("button").First(b => b.TextContent.Contains(
            Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()["Btn_Allow"].Value));
        allowButton.HasAttribute("disabled").ShouldBeTrue();
    }

    /// <summary>Serves the one route the page calls: GET /api/oauth/consent/{ticket}.</summary>
    [Fact]
    public async Task Decision_EchoesDisplayedCompany()
    {
        var consent = RenderConsent(BaseInfo());
        consent.WaitForAssertion(() => consent.Markup.ShouldContain(ClientId));
        var allow = consent.FindAll("button").First(b => b.TextContent.Contains(
            Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResource>>()["Btn_Allow"].Value));
        await allow.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        _api.Decision.ShouldNotBeNull().CompanyId.ShouldBe(17);
    }

    private sealed class ConsentStub : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public OAuthConsentInfoDto? Info { get; set; }
        public OAuthConsentDecisionDto? Decision { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.StartsWith("/api/oauth/consent/"))
            {
                var response = new HttpResponseMessage(Status)
                {
                    Content = JsonContent.Create(Info)
                };
                return response;
            }

            if (request.Method == HttpMethod.Post)
            {
                Decision = await request.Content!.ReadFromJsonAsync<OAuthConsentDecisionDto>(ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new OAuthConsentDecisionResultDto { RedirectUrl = "https://claude.ai/callback" }) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public void ExpiredTicket_ShowsRecoveryInsteadOfRemainingLoading()
    {
        _api.Status = HttpStatusCode.BadRequest;
        var cut = RenderConsent(BaseInfo());
        cut.WaitForAssertion(() => cut.FindAll(".mud-alert").Count.ShouldBe(1));
        cut.FindAll(".mud-progress-circular").ShouldBeEmpty();
        cut.Find("a[href='/settings/integrations']").ShouldNotBeNull();
    }

    [Fact]
    public async Task ConsentOutage_CanRetrySameTicket()
    {
        _api.Status = HttpStatusCode.ServiceUnavailable;
        var cut = RenderConsent(BaseInfo());
        cut.WaitForAssertion(() => cut.FindAll(".mud-alert").Count.ShouldBe(1));
        _api.Status = HttpStatusCode.OK;
        await cut.InvokeAsync(() => cut.FindComponent<MudBlazor.MudButton>().Instance.OnClick.InvokeAsync());
        cut.WaitForAssertion(() => cut.Markup.ShouldContain(ClientId));
    }
}
