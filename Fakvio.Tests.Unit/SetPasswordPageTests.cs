// ============================================================================
// SetPasswordPageTests — coverage for PR #175 (issue #152).
//
// UserInvitationTests covers the service, UserControllerSetPasswordTests the HTTP
// endpoint and UserApiServiceTests the client call. This file covers the last hop,
// the one the user actually sees: the page must render an orange warning — not the
// green "Done, log in" — when the API reports WorkspaceReady = false.
//
// The page is rendered over a real UserApiService wired to a stubbed HTTP handler,
// so the assertions cover the whole client half of the chain, response body included.
// ============================================================================

using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Bunit.TestDoubles;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the anonymous /set-password page.
/// </summary>
public class SetPasswordPageTests : BunitContext, IAsyncLifetime
{
    private const string InvitationToken = "valid-invitation-token";
    private const string Password = "MySecurePassword123";

    // MudBlazor's PopoverService only supports async disposal; xunit v2 disposes
    // test classes synchronously, so route disposal through IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public SetPasswordPageTests()
    {
        Services.AddMudServices();
        Services.AddLogging();

        // Real localization (same wiring as AddSharedUiServices) instead of a substitute:
        // it also proves the two new resource keys exist — a missing key would render
        // its own name and the message assertions below would fail.
        Services.AddLocalization(options => options.ResourcesPath = "Resources");

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>
    /// Renders the page for an invitation link whose token validates, with the API
    /// answering the set-password call with the given readiness flag.
    /// </summary>
    private IRenderedComponent<SetPassword> RenderPageWhereApiReports(bool workspaceReady)
        => RenderPage(workspaceReady, HttpStatusCode.OK);

    /// <summary>
    /// Renders the page for a valid invitation link where the set-password call itself
    /// answers with an HTTP error instead of a result.
    /// </summary>
    private IRenderedComponent<SetPassword> RenderPageWhereApiFailsWith(HttpStatusCode status)
        => RenderPage(workspaceReady: true, setPasswordStatus: status);

    /// <summary>
    /// Renders the page for an invitation link whose token validates, with the API
    /// answering the set-password call with the given status and readiness flag.
    /// </summary>
    private IRenderedComponent<SetPassword> RenderPage(bool workspaceReady, HttpStatusCode setPasswordStatus)
    {
        RegisterUserApiServiceAnswering(workspaceReady, setPasswordStatus);

        // The token arrives as a query parameter ([SupplyParameterFromQuery]),
        // so the page has to be reached through the URL, not through a parameter.
        Services.GetRequiredService<BunitNavigationManager>()
            .NavigateTo($"/set-password?token={InvitationToken}");

        return Render<SetPassword>();
    }

    /// <summary>
    /// Registers a real UserApiService over a stubbed handler: the token always validates,
    /// and set-password always answers 200 with PasswordSet = true and the given readiness.
    /// </summary>
    private void RegisterUserApiServiceAnswering(bool workspaceReady, HttpStatusCode setPasswordStatus)
    {
        var handler = new SetPasswordApiStub(workspaceReady, setPasswordStatus);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        Services.AddSingleton(factory);
        Services.AddSingleton(Substitute.For<AuthenticationStateProvider>());
        Services.AddSingleton<UserApiService>();
    }

    /// <summary>Fills both password fields and submits the form.</summary>
    private static void SubmitPassword(IRenderedComponent<SetPassword> page)
    {
        var inputs = page.FindAll("input");
        inputs.Count.ShouldBe(2, "the form has a password field and its confirmation");

        inputs[0].Change(Password);
        page.FindAll("input")[1].Change(Password);

        page.Find("button").Click();
    }

    /// <summary>Resolves a resource key exactly the way the page does.</summary>
    private string Localized(string key)
    {
        var localized = Services.GetRequiredService<IStringLocalizer<SharedResource>>()[key];
        localized.ResourceNotFound.ShouldBeFalse($"resource key '{key}' is missing from SharedResource.resx");
        return localized.Value;
    }

    /// <summary>
    /// Happy path: a provisioned workspace still gets the plain success message.
    /// Guards against the fix over-correcting into "warn always".
    /// </summary>
    [Fact]
    public void SetPassword_WorkspaceReady_ShowsSuccessAlert()
    {
        var page = RenderPageWhereApiReports(workspaceReady: true);

        SubmitPassword(page);

        var alert = page.Find("div.mud-alert");
        alert.ClassList.ShouldContain(c => c.Contains("success"));
        page.Markup.ShouldContain(Localized("SetPassword_SuccessMessage"));
    }

    /// <summary>
    /// Regression test for issue #152 where the user could see it: provisioning failed,
    /// so the page must warn instead of reporting the workspace as usable.
    /// </summary>
    [Fact]
    public void SetPassword_WorkspaceNotReady_ShowsWarningInsteadOfSuccess()
    {
        var page = RenderPageWhereApiReports(workspaceReady: false);

        SubmitPassword(page);

        var alert = page.Find("div.mud-alert");
        alert.ClassList.ShouldContain(c => c.Contains("warning"));

        page.Markup.ShouldContain(Localized("SetPassword_WorkspaceNotReady"));
        page.Markup.ShouldContain(Localized("SetPassword_WorkspaceNotReadyHint"));

        // The whole point of the issue: no "Done, log in" for a workspace that does not exist.
        page.Markup.ShouldNotContain(Localized("SetPassword_SuccessMessage"));
    }

    /// <summary>
    /// The snackbar is the second channel carrying the same verdict — it must not
    /// pop a green success toast over the warning alert.
    /// </summary>
    [Fact]
    public void SetPassword_WorkspaceNotReady_ShowsWarningSnackbar()
    {
        var page = RenderPageWhereApiReports(workspaceReady: false);

        SubmitPassword(page);

        var snackbars = Services.GetRequiredService<ISnackbar>().ShownSnackbars.ToList();

        snackbars.Count.ShouldBe(1);
        snackbars[0].Severity.ShouldBe(Severity.Warning);
    }

    /// <summary>
    /// Characterization of a known gap, shown where it reaches the user.
    ///
    /// UserApiService turns every non-2xx into PasswordSet = false, so a server fault is
    /// rendered as "the token may have expired" and sends the user off to ask for a new
    /// invitation for a token that was never the problem. Pinned rather than fixed: the
    /// residual window is narrow (see UserApiServiceTests) and separating server faults
    /// from token faults is a change of its own, which must turn this test red.
    /// </summary>
    [Fact]
    public void SetPassword_ServerFault_IsShownAsAnExpiredToken_KnownGap()
    {
        var page = RenderPageWhereApiFailsWith(HttpStatusCode.InternalServerError);

        SubmitPassword(page);

        page.Markup.ShouldContain(Localized("SetPassword_SetFailed"));
        page.Markup.ShouldNotContain(Localized("SetPassword_SuccessMessage"));
    }

    /// <summary>
    /// HttpMessageHandler stub for the two anonymous endpoints the page calls:
    /// the invitation token always validates, set-password reports the configured readiness.
    /// </summary>
    private sealed class SetPasswordApiStub(bool workspaceReady, HttpStatusCode setPasswordStatus)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Token validation always succeeds — only the set-password call carries the
            // configured status, so a fault test still reaches the password form.
            var isTokenValidation = request.RequestUri!.AbsolutePath.EndsWith("/validate-invitation");

            var body = isTokenValidation
                ? new { isValid = true }
                : (object)new { passwordSet = true, workspaceReady };

            return Task.FromResult(new HttpResponseMessage(isTokenValidation ? HttpStatusCode.OK : setPasswordStatus)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(body, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    }),
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
