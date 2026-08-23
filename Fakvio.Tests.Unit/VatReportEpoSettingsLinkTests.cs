using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Bunit;
using Fakvio.Contracts.Dto.Client;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for the EPO_HEADER_INCOMPLETE warning on /vat-report (issue #158).
///
/// The bug: whoever hit the warning was offered a link to /my-company — a page that had
/// no EPO fields at all, and whose settings sections are hidden from plain Users anyway.
/// The link is therefore only shown to the roles that can edit the fields there
/// (Admin / SysAdmin); everyone else is told to ask their administrator.
/// </summary>
public class VatReportEpoSettingsLinkTests : BunitContext, IAsyncLifetime
{
    // MudBlazor's PopoverService only supports async disposal; xunit disposes test
    // classes synchronously, so route disposal through IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public VatReportEpoSettingsLinkTests()
    {
        // The page uses MudDatePicker / MudSelect, which create popovers. There is no
        // MudPopoverProvider in a bUnit render tree, so the guard has to be switched off.
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Localizer returns the key itself, so assertions can target keys instead of
        // translations (the page renders both CZ and EN from the same markup).
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);

        // Both API services talk to the same stub backend: the issuer is a VAT payer
        // (so the EPO section renders) and every EPO download fails with
        // EPO_HEADER_INCOMPLETE (so the warning renders).
        var httpClient = new HttpClient(new EpoIncompleteBackendHandler())
        {
            BaseAddress = new Uri("https://test.local")
        };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);
        Services.AddSingleton(factory);

        Services.AddSingleton(sp => new VatReportApiService(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<VatReportApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));
        Services.AddSingleton(sp => new ClientApiService(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ClientApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));
    }

    /// <summary>
    /// Renders /vat-report for a user in the given roles and triggers the DPHDP3
    /// download, which the stub backend answers with EPO_HEADER_INCOMPLETE.
    /// </summary>
    private IRenderedComponent<VatReport> RenderWithFailedEpoDownload(params string[] roles)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("ucetni@example.cz");
        auth.SetRoles(roles);

        var cut = Render<VatReport>();

        // Buttons are located by their (stubbed) label — the page renders several
        // MudBlazor buttons and none of them carries a stable id.
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("EpoDownloadVatReturn"));
        cut.FindAll("button").First(b => b.TextContent.Contains("EpoDownloadVatReturn")).Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("EpoHeaderIncomplete"));
        return cut;
    }

    [Fact]
    public void Admin_IsOfferedTheSettingsLink()
    {
        var cut = RenderWithFailedEpoDownload("Admin");

        cut.Markup.ShouldContain("EpoGoToSettings");
        cut.Markup.ShouldNotContain("EpoHeaderIncompleteAskAdmin");
    }

    [Fact]
    public void SysAdmin_IsOfferedTheSettingsLink()
    {
        var cut = RenderWithFailedEpoDownload("SysAdmin");

        cut.Markup.ShouldContain("EpoGoToSettings");
    }

    [Fact]
    public void Admin_SettingsLink_NavigatesToMyCompany()
    {
        var cut = RenderWithFailedEpoDownload("Admin");

        cut.FindAll("button").First(b => b.TextContent.Contains("EpoGoToSettings")).Click();

        Services.GetRequiredService<NavigationManager>().Uri
            .ShouldEndWith("/my-company");
    }

    [Fact]
    public void PlainUser_IsToldToAskAnAdmin_InsteadOfBeingSentToAPageThatCannotHelp()
    {
        var cut = RenderWithFailedEpoDownload("User");

        // This is the regression the issue reported: the link used to be shown to
        // everyone, including users who cannot see the settings section at all.
        cut.Markup.ShouldNotContain("EpoGoToSettings");
        cut.Markup.ShouldContain("EpoHeaderIncompleteAskAdmin");
    }

    /// <summary>
    /// Stub backend for the page: the issuer endpoint returns a VAT payer, every other
    /// (EPO download) request returns 400 EPO_HEADER_INCOMPLETE with the missing fields.
    /// </summary>
    private sealed class EpoIncompleteBackendHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/api/client/issuer", StringComparison.Ordinal))
            {
                var issuer = new ClientDto { Id = 42, CompanyName = "Testovací s.r.o.", IsVatPayer = true };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(issuer)
                });
            }

            var error = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["code"] = "EPO_HEADER_INCOMPLETE",
                ["message"] = "Missing fields",
                ["missingFields"] = new[] { "EpoTaxOfficeCode", "EpoTaxOfficeBranchCode" }
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(error, Encoding.UTF8, "application/json")
            });
        }
    }
}
