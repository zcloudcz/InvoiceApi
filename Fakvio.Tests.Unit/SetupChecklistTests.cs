using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

    /// <summary>Two companies of one tenant — the dashboard reports for all of them at once.</summary>
    private const string FirstIssuerName = "První firma s.r.o.";
    private const string SecondIssuerName = "Druhá firma s.r.o.";

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
        AddAuthorization().SetRoles("Admin");
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
    public void BlockingAndWarning_AreListedUnderTheirOwnHeadings_SoSeverityIsNotCarriedByColourAlone()
    {
        _backend.Report = ReportWith(
            // Warning first on purpose: TenantReadinessService does not sort the report, so the
            // component's own grouping is the only thing that puts blockers above hints.
            Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning, "/company-settings"),
            Issue(ReadinessCodes.IssuerAddressIncomplete, EReadinessSeverity.Blocking, "/my-company"));

        var cut = Render<SetupChecklist>();
        cut.WaitForAssertion(() => cut.FindAll("div.setup-checklist-group").Count.ShouldBe(2));

        var groups = cut.FindAll("div.setup-checklist-group");

        // Asserted per group, not "somewhere in the markup": severity has to be readable as text
        // (a screen reader gets nothing from an icon colour, and red vs orange is the pair
        // colour-blind users are least able to tell apart). Swapping the two severities has to
        // fail here — with a flat list it changed nothing a test could see.
        groups[0].TextContent.ShouldContain("Readiness_BlockingTitle");
        groups[0].TextContent.ShouldContain("Readiness_Code_ISSUER_ADDRESS_INCOMPLETE");
        groups[0].TextContent.ShouldNotContain("Readiness_Code_EPO_HEADER_INCOMPLETE");
        groups[0].InnerHtml.ShouldContain("mud-error-text");
        groups[0].InnerHtml.ShouldNotContain("mud-warning-text");

        groups[1].TextContent.ShouldContain("Readiness_WarningTitle");
        groups[1].TextContent.ShouldContain("Readiness_Code_EPO_HEADER_INCOMPLETE");
        groups[1].TextContent.ShouldNotContain("Readiness_Code_ISSUER_ADDRESS_INCOMPLETE");
        groups[1].InnerHtml.ShouldContain("mud-warning-text");
        groups[1].InnerHtml.ShouldNotContain("mud-error-text");
    }

    [Fact]
    public void OneSeverityOnly_RendersOnlyThatHeading_SoAReadyEnoughTenantIsNotWarnedAboutNothing()
    {
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning, "/company-settings"));

        var cut = Render<SetupChecklist>();
        cut.WaitForAssertion(() => cut.FindAll("div.setup-checklist-group").Count.ShouldBe(1));

        // An empty "before you start invoicing" heading would read as a blocker that is not there.
        cut.Markup.ShouldContain("Readiness_WarningTitle");
        cut.Markup.ShouldNotContain("Readiness_BlockingTitle");
    }

    [Fact]
    public void UnreadableDeferralFlag_StillShowsTheGuide_SoAStorageHiccupDoesNotCostTheDashboard()
    {
        // Only reachable through a value written by hand or by an older schema — nobody else writes
        // this key. Cheap to guard anyway: an exception out of OnInitializedAsync is the whole page
        // for a "remind me later" flag, and not knowing the flag just means showing the guide.
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerAddressIncomplete, EReadinessSeverity.Blocking, "/my-company"));
        _storage.GetItemAsync<bool>(DeferStorageKey)
            .Returns(ValueTask.FromException<bool>(new JsonException("corrupted flag")));

        var cut = Render<SetupChecklist>();

        cut.WaitForAssertion(() => cut.FindAll("div.mud-card").Count.ShouldBe(1));
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

        // The open card carries one button for non-admins — "remind me later".
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

    [Fact]
    public void DeferredGuide_CountsIssuesNotGroups_SoTheCollapsedReminderStaysHonest()
    {
        // Three issues in two groups, on purpose: counting groups would produce an equally
        // plausible-looking number. Once the guide is parked this button is the whole visible
        // surface, so a wrong count here is what the user reads every day until they reopen it.
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerAddressIncomplete, EReadinessSeverity.Blocking, "/my-company"),
            Issue(ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking, "/my-company"),
            Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning, "/company-settings"));
        _storage.GetItemAsync<bool>(DeferStorageKey).Returns(true);

        var cut = Render<SetupChecklist>();

        cut.WaitForAssertion(() =>
            cut.Find("button").TextContent.ShouldContain("SetupChecklist_Title (3)"));
    }

    [Fact]
    public void IssuesOfSeveralIssuers_NameTheirCompany_SoTheTenantWideListStaysUnambiguous()
    {
        // The dashboard speaks for the whole tenant, so one rule can fire for two companies at
        // once. Without the issuer name both rows read identically and neither says who to fix.
        // The name is appended by the shared ReadinessIssueText.Describe — a component that
        // localized the code itself would look right in every other test in this file.
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking,
                "/my-company", issuerName: FirstIssuerName),
            Issue(ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking,
                "/my-company", issuerName: SecondIssuerName));

        var cut = Render<SetupChecklist>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.ShouldContain(FirstIssuerName);
            cut.Markup.ShouldContain(SecondIssuerName);
        });
    }

    [Fact]
    public void IssueWithoutMissingFields_RendersNoFieldList_SoNoBlankLineIsLeftBehind()
    {
        // MissingFields is optional: a rule such as "no active number sequence" names no field.
        // Rendering the caption unconditionally would leave an empty grey line under the item.
        _backend.Report = ReportWith(
            Issue(ReadinessCodes.NumberSequenceMissing, EReadinessSeverity.Blocking, "/number-sequences"));

        var cut = Render<SetupChecklist>();
        cut.WaitForAssertion(() => cut.FindAll("div.setup-checklist-group").Count.ShouldBe(1));

        cut.FindAll(".mud-typography-caption").ShouldBeEmpty();
    }

    /// <summary>Convenience factory for a readiness issue with the fields the checklist reads.</summary>
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
