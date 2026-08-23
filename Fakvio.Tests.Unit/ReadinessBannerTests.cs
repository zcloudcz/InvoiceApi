using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for <see cref="ReadinessBanner"/> (issue #215).
///
/// The banner is the preventive half of the invoice completion gate (#206): it lists the same
/// blocking issues the API would refuse an issue attempt with, before the user clicks. These
/// tests pin down the four properties that make it useful rather than decorative — it stays
/// silent when there is nothing to say, it separates "you are blocked" from "heads up", every
/// item carries the API-supplied fix route, and a broken readiness call degrades to silence
/// instead of taking the hosting page down.
/// </summary>
public class ReadinessBannerTests : BunitContext, IAsyncLifetime
{
    private readonly ReadinessBackendHandler _backend = new();

    // MudBlazor's PopoverService only supports async disposal; xunit disposes test classes
    // synchronously, so route disposal through IAsyncLifetime (same as MyCompanyEpoSectionTests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public ReadinessBannerTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Localizer echoes the key, so assertions can target keys instead of translations.
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);

        var httpClient = new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);
        Services.AddSingleton(factory);

        Services.AddSingleton(sp => new ReadinessApiService(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ReadinessApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));

        AddAuthorization().SetAuthorized("ucetni@example.cz");
    }

    /// <summary>Convenience factory for a readiness issue with the fields the banner reads.</summary>
    private static ReadinessIssueDto Issue(
        string code,
        EReadinessSeverity severity,
        string fixRoute,
        string? issuerName = null,
        params string[] missingFields) => new()
        {
            Code = code,
            Severity = severity,
            FixRoute = fixRoute,
            IssuerName = issuerName,
            MissingFields = missingFields.ToList()
        };

    [Fact]
    public void ReadyTenant_RendersNothing_SoTheDashboardStaysClean()
    {
        _backend.Report = new ReadinessReportDto();

        var cut = Render<ReadinessBanner>();

        cut.WaitForAssertion(() => _backend.CallCount.ShouldBe(1));
        cut.Markup.Trim().ShouldBeEmpty();
    }

    [Fact]
    public void BlockingAndWarning_RenderAsTwoSeparateAlerts_SoTheUserSeesWhatActuallyBlocksThem()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerAddressIncomplete, EReadinessSeverity.Blocking, "/my-company"),
            Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning, "/company-settings"));

        var cut = Render<ReadinessBanner>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Readiness_BlockingTitle"));

        // Two alerts, not one mixed list — otherwise "blocking" and "warning" look identical.
        var alerts = cut.FindAll("div.mud-alert");
        alerts.Count.ShouldBe(2, "blocking and warning issues must not share one alert");

        // MudBlazor encodes the severity in the alert's CSS class; Error vs Warning is the
        // visual distinction the acceptance criteria ask for.
        alerts[0].GetAttribute("class").ShouldContain("error");
        alerts[1].GetAttribute("class").ShouldContain("warning");

        cut.Markup.ShouldContain("Readiness_WarningTitle");
    }

    [Fact]
    public void WarningsOnly_RenderNoErrorAlert_SoAnEpoHintIsNotMistakenForABlocker()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning, "/company-settings"));

        var cut = Render<ReadinessBanner>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Readiness_WarningTitle"));

        cut.FindAll("div.mud-alert").Count.ShouldBe(1);
        cut.Markup.ShouldNotContain("Readiness_BlockingTitle");
    }

    [Fact]
    public void EveryIssue_LinksToItsOwnFixRoute_SoTheBannerIsActionable()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking, "/my-company"),
            Issue(ReadinessCodes.NumberSequenceMissing, EReadinessSeverity.Blocking, "/number-sequences"));

        var cut = Render<ReadinessBanner>();
        cut.WaitForAssertion(() => cut.FindAll("a").Count.ShouldBe(2));

        // The route comes from the DTO — the UI must not derive it from the code.
        cut.FindAll("a").Select(a => a.GetAttribute("href"))
            .ShouldBe(new[] { "/my-company", "/number-sequences" });
    }

    [Fact]
    public void IssueText_UsesTheCodeAsLocalizationKey_AndAppendsTheIssuerName()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerTaxNumberMissing, EReadinessSeverity.Blocking,
                "/my-company", issuerName: "Druhá firma s.r.o.", missingFields: "TaxNumber"));

        var cut = Render<ReadinessBanner>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Readiness_Code_ISSUER_TAX_NUMBER_MISSING"));

        // A tenant can have several issuers — the banner has to say which one is incomplete.
        cut.Markup.ShouldContain("Druhá firma s.r.o.");

        // Same field names the TENANT_NOT_READY 400 lists, so the two surfaces agree.
        cut.Markup.ShouldContain("TaxNumber");
    }

    [Fact]
    public void UnknownCode_FallsBackToAGenericSentence_SoNoRawCodeLeaksToTheUser()
    {
        _backend.Report = ReportWith(
            Issue("SOMETHING_THE_UI_DOES_NOT_KNOW_YET", EReadinessSeverity.Blocking, "/my-company"));

        // Only this one key is missing; every other key still echoes itself.
        var localizer = Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        localizer["Readiness_Code_SOMETHING_THE_UI_DOES_NOT_KNOW_YET"].Returns(
            new LocalizedString("Readiness_Code_SOMETHING_THE_UI_DOES_NOT_KNOW_YET",
                "Readiness_Code_SOMETHING_THE_UI_DOES_NOT_KNOW_YET", resourceNotFound: true));

        var cut = Render<ReadinessBanner>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Readiness_Code_Unknown"));

        cut.Markup.ShouldNotContain("SOMETHING_THE_UI_DOES_NOT_KNOW_YET");
    }

    [Fact]
    public void IssuerId_IsForwardedToTheApi_SoAnUnrelatedIssuerDoesNotRaiseAFalseAlarm()
    {
        _backend.Report = new ReadinessReportDto();

        var cut = Render<ReadinessBanner>(p => p.Add(c => c.IssuerId, 77L));

        cut.WaitForAssertion(() => _backend.LastQuery.ShouldNotBeNull());
        _backend.LastQuery.ShouldContain("issuerId=77");
    }

    [Fact]
    public void WithoutIssuerId_TheWholeTenantIsChecked()
    {
        _backend.Report = new ReadinessReportDto();

        var cut = Render<ReadinessBanner>();

        cut.WaitForAssertion(() => _backend.CallCount.ShouldBe(1));
        _backend.LastQuery.ShouldBeEmpty();
    }

    [Fact]
    public void FailedReadinessCall_RendersNothingAndDoesNotThrow_SoTheHostPageSurvives()
    {
        _backend.StatusCode = HttpStatusCode.InternalServerError;

        // Render must not throw — a readiness hint is decoration, never a page killer.
        var cut = Render<ReadinessBanner>();

        cut.WaitForAssertion(() => _backend.CallCount.ShouldBe(1));
        cut.Markup.Trim().ShouldBeEmpty();
    }

    private static ReadinessReportDto ReportWith(params ReadinessIssueDto[] issues)
        => new() { Issues = issues.ToList() };

    /// <summary>Stub backend answering GET /api/readiness.</summary>
    private sealed class ReadinessBackendHandler : HttpMessageHandler
    {
        /// <summary>Payload returned on success.</summary>
        public ReadinessReportDto Report { get; set; } = new();

        /// <summary>Status answered to the readiness call; non-OK exercises the degradation path.</summary>
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        /// <summary>Query string of the last readiness request, without the leading '?'.</summary>
        public string? LastQuery { get; private set; }

        /// <summary>How many readiness requests arrived — guards against a silent no-op render.</summary>
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!path.EndsWith("/api/readiness", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            CallCount++;
            LastQuery = request.RequestUri?.Query.TrimStart('?') ?? string.Empty;

            var response = StatusCode == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Report) }
                : new HttpResponseMessage(StatusCode);

            return Task.FromResult(response);
        }
    }
}
