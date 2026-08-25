using System.Net;
using System.Net.Http.Json;
using Blazored.LocalStorage;
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
/// bUnit render tests for <see cref="SetupChecklist"/> (issue #210, story #150).
///
/// The checklist replaced a hard-coded "Quick Start" timeline, so the properties worth pinning
/// down are exactly the ones the static version could not have: the list comes from the live
/// readiness report, "remind me later" only parks the guide instead of dismissing it forever,
/// the deferral survives a reload — and completion is never stored, so a tenant that finishes
/// its setup while the guide is deferred sees the guide disappear rather than a stale reminder.
/// </summary>
public class SetupChecklistTests : BunitContext, IAsyncLifetime
{
    /// <summary>
    /// Hard-coded on purpose (the component keeps it private). Renaming the key silently resets
    /// the deferral for every existing user, so it should cost a failing test.
    /// </summary>
    private const string DeferStorageKey = "setupChecklistDeferred";

    private readonly ReadinessBackendHandler _backend = new();
    private readonly ILocalStorageService _storage = Substitute.For<ILocalStorageService>();

    // MudBlazor's PopoverService only supports async disposal; xunit disposes test classes
    // synchronously, so route disposal through IAsyncLifetime (same as ReadinessBannerTests).
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public SetupChecklistTests()
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

        // Substituted instead of the real Blazored implementation, which would need a browser.
        // An unstubbed GetItemAsync<bool> returns false — exactly what a first-time visitor has.
        Services.AddSingleton(_storage);

        AddAuthorization().SetAuthorized("ucetni@example.cz");
    }

    [Fact]
    public void OpenIssues_AreListedWithTheirFixRoutes_SoTheGuideIsActionable()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking,
                "/my-company", missingFields: "AccountNumber"),
            Issue(ReadinessCodes.NumberSequenceMissing, EReadinessSeverity.Blocking,
                "/number-sequences"));

        var cut = Render<SetupChecklist>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("SetupChecklist_Title"));

        // The wording comes from the code used as a localization key (shared with ReadinessBanner).
        cut.Markup.ShouldContain("Readiness_Code_ISSUER_BANK_ACCOUNT_MISSING");
        cut.Markup.ShouldContain("Readiness_Code_NUMBER_SEQUENCE_MISSING");

        // Routes come from the DTO — the UI must not derive them from the code.
        cut.FindAll("a").Select(a => a.GetAttribute("href"))
            .ShouldBe(new[] { "/my-company", "/number-sequences" });

        // Same field names the 400 TENANT_NOT_READY response lists, so a user who does hit the
        // refusal recognises what the checklist had already told them.
        cut.Markup.ShouldContain("AccountNumber");
    }

    [Fact]
    public void BlockingAndWarning_AreVisuallyDistinct_SoAnEpoHintIsNotMistakenForABlocker()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerAddressIncomplete, EReadinessSeverity.Blocking, "/my-company"),
            Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning, "/company-settings"));

        var cut = Render<SetupChecklist>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("SetupChecklist_Title"));

        // MudBlazor encodes the icon colour in a CSS class. Without the distinction an EPO hint
        // would look exactly like something that blocks invoicing today.
        cut.Markup.ShouldContain("mud-error-text");
        cut.Markup.ShouldContain("mud-warning-text");
    }

    [Fact]
    public void ReadyTenant_RendersNothing_SoAFinishedSetupLeavesTheDashboardClean()
    {
        _backend.Report = new ReadinessReportDto();

        var cut = Render<SetupChecklist>();

        // CallCount++ runs synchronously inside SendAsync, before the response is delivered, so
        // waiting on it alone would prove nothing about the markup — assert both together.
        cut.WaitForAssertion(() =>
        {
            _backend.CallCount.ShouldBe(1);
            cut.Markup.Trim().ShouldBeEmpty();
        });
    }

    [Fact]
    public void Defer_CollapsesTheGuideAndStoresTheFlag_SoItCanComeBackAfterAReload()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerRegistrationNumberMissing, EReadinessSeverity.Blocking, "/my-company"));

        var cut = Render<SetupChecklist>();
        cut.WaitForAssertion(() => cut.FindAll("div.mud-card").Count.ShouldBe(1));

        // The open card carries exactly one button — "remind me later".
        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            // Collapsed, not dismissed: the entry point stays, the item list goes away.
            cut.FindAll("div.mud-card").ShouldBeEmpty();
            cut.Markup.ShouldContain("SetupChecklist_Title");
            cut.Markup.ShouldNotContain("Readiness_Code_ISSUER_REGISTRATION_NUMBER_MISSING");
        });

        _storage.Received(1).SetItemAsync(DeferStorageKey, true);
    }

    [Fact]
    public void StoredDeferral_ShowsOnlyTheEntryPoint_SoTheChoiceSurvivesAReload()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerRegistrationNumberMissing, EReadinessSeverity.Blocking, "/my-company"));
        _storage.GetItemAsync<bool>(DeferStorageKey).Returns(true);

        var cut = Render<SetupChecklist>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain("SetupChecklist_Title");
            cut.FindAll("div.mud-card").ShouldBeEmpty();
        });
    }

    [Fact]
    public void Resume_ReopensTheGuideAndForgetsTheDeferral_SoItIsNotAOneShotWizard()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerRegistrationNumberMissing, EReadinessSeverity.Blocking, "/my-company"));
        _storage.GetItemAsync<bool>(DeferStorageKey).Returns(true);

        var cut = Render<SetupChecklist>();
        cut.WaitForAssertion(() => cut.FindAll("button").Count.ShouldBe(1));

        cut.Find("button").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("div.mud-card").Count.ShouldBe(1);
            cut.Markup.ShouldContain("Readiness_Code_ISSUER_REGISTRATION_NUMBER_MISSING");
        });

        // Removed, not written as false: the next visit has to read "no flag stored" the same
        // way a brand new browser does.
        _storage.Received(1).RemoveItemAsync(DeferStorageKey);
    }

    [Fact]
    public void DeferredButFinishedSetup_RendersNothing_BecauseDoneIsNeverStored()
    {
        // The reason the deferral is the only persisted bit: a tenant whose settings were
        // completed in the meantime (possibly by a colleague) must not keep seeing a reminder
        // just because an old flag says the guide was parked.
        _backend.Report = new ReadinessReportDto();
        _storage.GetItemAsync<bool>(DeferStorageKey).Returns(true);

        var cut = Render<SetupChecklist>();

        cut.WaitForAssertion(() =>
        {
            _backend.CallCount.ShouldBe(1);
            cut.Markup.Trim().ShouldBeEmpty();
        });
    }

    [Fact]
    public void FailedReadinessCall_RendersNothingAndDoesNotThrow_SoTheDashboardSurvives()
    {
        // The guide sits at the top of the dashboard; an exception escaping its lifecycle method
        // is an unhandled exception in Blazor WASM, i.e. a dead app over a decorative card.
        _backend.StatusCode = HttpStatusCode.InternalServerError;

        var cut = Render<SetupChecklist>();

        cut.WaitForAssertion(() =>
        {
            _backend.CallCount.ShouldBe(1);
            cut.Markup.Trim().ShouldBeEmpty();
        });
    }

    /// <summary>Convenience factory for a readiness issue with the fields the checklist reads.</summary>
    private static ReadinessIssueDto Issue(
        string code,
        EReadinessSeverity severity,
        string fixRoute,
        params string[] missingFields) => new()
        {
            Code = code,
            Severity = severity,
            FixRoute = fixRoute,
            MissingFields = missingFields.ToList()
        };

    private static ReadinessReportDto ReportWith(params ReadinessIssueDto[] issues)
        => new() { Issues = issues.ToList() };

    /// <summary>Stub backend answering GET /api/readiness.</summary>
    private sealed class ReadinessBackendHandler : HttpMessageHandler
    {
        /// <summary>Payload returned on success.</summary>
        public ReadinessReportDto Report { get; set; } = new();

        /// <summary>Status answered to the readiness call; non-OK exercises the degradation path.</summary>
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

        /// <summary>How many readiness requests arrived — guards against a silent no-op render.</summary>
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!path.EndsWith("/api/readiness", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            CallCount++;

            if (StatusCode != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(StatusCode));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Report)
            });
        }
    }
}
