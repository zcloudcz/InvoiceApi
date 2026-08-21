using System.Net;
using System.Net.Http.Json;
using Bunit;
using Fakvio.Contracts.Dto.Client;
using Fakvio.Contracts.Dto.CloudStorage;
using Fakvio.Contracts.Dto.CompanySettings;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Pages;
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
/// bUnit render tests for the EPO section on /my-company (issue #158).
///
/// EpoSettingsSectionTests render the section standalone, VatReportEpoSettingsLinkTests
/// pin down who is offered the link. Neither of them proves the actual fix: that the
/// page the link points at really hosts the section, and that it hosts it for exactly
/// the roles the link is offered to. That is what this file covers — remove the section
/// from MyCompany.razor, or change one of the two role lists, and these tests go red.
/// </summary>
public class MyCompanyEpoSectionTests : BunitContext, IAsyncLifetime
{
    // Company the stub backend answers for; MyCompany.razor loads it via GET /api/client/issuer.
    private const long CompanyId = 42;
    private const string CompanyName = "Testovací s.r.o.";

    private readonly CompanyBackendHandler _backend = new();

    // MudBlazor's PopoverService only supports async disposal; xunit disposes test
    // classes synchronously, so route disposal through IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public MyCompanyEpoSectionTests()
    {
        // The page uses MudSelect / MudDatePicker, which create popovers. There is no
        // MudPopoverProvider in a bUnit render tree, so the guard has to be switched off.
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Localizer returns the key itself, so assertions can target keys instead of
        // translations (the page renders both CZ and EN from the same markup).
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);

        var httpClient = new HttpClient(_backend) { BaseAddress = new Uri("https://test.local") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("InvoiceAPI").Returns(httpClient);
        Services.AddSingleton(factory);

        Services.AddSingleton(sp => new ClientApiService(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ClientApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));
        Services.AddSingleton(sp => new CompanySettingsApiService(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<CompanySettingsApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));
        Services.AddSingleton(sp => new CloudStorageApiService(
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<CloudStorageApiService>.Instance,
            sp.GetRequiredService<AuthenticationStateProvider>()));

        // The page hosts several unrelated editors, each pulling in its own API service.
        // They are stubbed out so this file only depends on the EPO section and the two
        // services that feed it — a new editor on the page must not break these tests.
        ComponentFactories.AddStub<AddressListEditor>();
        ComponentFactories.AddStub<BankAccountListEditor>();
        ComponentFactories.AddStub<RecognizedCounterpartyEditor>();
        ComponentFactories.AddStub<InvoiceMailboxCard>();
        ComponentFactories.AddStub<FolderPicker>();
    }

    /// <summary>
    /// Renders /my-company for a user in the given roles and waits until the initial
    /// loads finished (the page shows a progress bar until then).
    /// </summary>
    private IRenderedComponent<MyCompany> RenderPageAs(params string[] roles)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized("ucetni@example.cz");
        auth.SetRoles(roles);

        var cut = Render<MyCompany>();
        // The page renders a progress bar until LoadCompany() returns; the company name
        // appearing in the markup means the initial loads are done.
        cut.WaitForAssertion(() => cut.Markup.ShouldContain(CompanyName));
        return cut;
    }

    [Fact]
    public void Admin_SeesTheEpoSection_SoTheLinkFromVatReportLeadsSomewhereUseful()
    {
        var cut = RenderPageAs("Admin");

        cut.FindComponents<EpoSettingsSection>().ShouldNotBeEmpty(
            "the /vat-report link sends an Admin here to fill in the EPO header fields");
    }

    [Fact]
    public void SysAdmin_SeesTheEpoSection()
    {
        var cut = RenderPageAs("SysAdmin");

        cut.FindComponents<EpoSettingsSection>().ShouldNotBeEmpty();
    }

    [Fact]
    public void PlainUser_DoesNotSeeTheEpoSection_WhichIsWhyVatReportTellsThemToAskAnAdmin()
    {
        var cut = RenderPageAs("User");

        // The two role lists must agree. If the section ever became visible here, the
        // "ask your administrator" message on /vat-report would send the user away from
        // a page that could have helped them.
        cut.FindComponents<EpoSettingsSection>().ShouldBeEmpty();
    }

    [Fact]
    public void Admin_SeesTheStoredEpoValues_BecauseThePageFeedsItsSettingsToTheSection()
    {
        _backend.Settings = SettingsWithEpoHeader();

        var cut = RenderPageAs("Admin");

        // Pins the Settings="_companySettings" wiring: the section itself is loaded from
        // the same record the SMTP section uses, not from a second API call.
        var values = cut.FindComponent<EpoSettingsSection>()
            .FindAll("input").Select(i => i.GetAttribute("value")).ToList();
        values.ShouldContain("451");
        values.ShouldContain("2001");
    }

    [Fact]
    public async Task Admin_Saving_SendsPartialUpdateWithTheEnteredEpoValues()
    {
        var cut = RenderPageAs("Admin");
        var section = cut.FindComponent<EpoSettingsSection>();
        var inputs = section.FindAll("input");
        inputs[0].Change("451");
        inputs[1].Change("2001");

        ClickSave(section);

        // The whole point of the issue: entering the values here must reach the endpoint
        // that /vat-report validates against.
        await cut.WaitForAssertionAsync(() => _backend.LastUpdate.ShouldNotBeNull());
        _backend.LastUpdate!.EpoTaxOfficeCode.ShouldBe(451);
        _backend.LastUpdate.EpoTaxOfficeBranchCode.ShouldBe(2001);
        // Partial update — the SMTP and AI values on the same record must stay untouched.
        _backend.LastUpdate.SmtpHost.ShouldBeNull();
        _backend.LastUpdate.AiDefaultProvider.ShouldBeNull();
    }

    [Fact]
    public async Task Admin_SavingWithBlankOptionalContacts_ReachesTheApiInsteadOfFailingValidation()
    {
        var cut = RenderPageAs("Admin");
        var section = cut.FindComponent<EpoSettingsSection>();
        var inputs = section.FindAll("input");
        inputs[0].Change("451");
        inputs[1].Change("2001");
        // Phone, e-mail and authorized person are left empty — the guides call them optional.

        ClickSave(section);

        // Regression guard for the round-1 defect, this time through the real page: an
        // empty contact e-mail used to be sent as "" and [ApiController] validation turned
        // the save into a 400 before the endpoint ran. The request must arrive at all.
        await cut.WaitForAssertionAsync(() => _backend.LastUpdate.ShouldNotBeNull());
        _backend.LastUpdate!.EpoContactEmail.ShouldBeNull();
        UpdateCompanySystemSettingsDtoValidationTests
            .AssertPassesApiValidation(_backend.LastUpdate);
    }

    /// <summary>Settings record with the two mandatory EPO header codes filled in.</summary>
    private static CompanySystemSettingsDto SettingsWithEpoHeader() => new()
    {
        Id = 1,
        CompanyId = CompanyId,
        EpoTaxOfficeCode = 451,
        EpoTaxOfficeBranchCode = 2001
    };

    /// <summary>
    /// Clicks the section's Save button. Located by its label because MudNumericField
    /// renders its own spinner buttons, so "the first button" is not the Save button.
    /// </summary>
    private static void ClickSave(IRenderedComponent<EpoSettingsSection> section)
        => section.FindAll("button").First(b => b.TextContent.Contains("Btn_Save")).Click();

    /// <summary>
    /// Stub backend for /my-company: it answers the three GETs the page fires on init and
    /// records the settings update the EPO section triggers.
    /// </summary>
    private sealed class CompanyBackendHandler : HttpMessageHandler
    {
        /// <summary>Settings returned by GET /api/company/{id}/settings.</summary>
        public CompanySystemSettingsDto Settings { get; set; } = new()
        {
            Id = 1,
            CompanyId = CompanyId
        };

        /// <summary>Body of the last PUT /api/company/{id}/settings, null until one arrives.</summary>
        public UpdateCompanySystemSettingsDto? LastUpdate { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.EndsWith("/api/client/issuer", StringComparison.Ordinal))
            {
                var issuer = new ClientDto
                {
                    Id = CompanyId,
                    CompanyName = CompanyName,
                    IsIssuer = true
                };
                return Json(issuer);
            }

            if (path.EndsWith($"/api/company/{CompanyId}/settings", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Put)
                {
                    LastUpdate = await request.Content!
                        .ReadFromJsonAsync<UpdateCompanySystemSettingsDto>(cancellationToken);
                }

                return Json(Settings);
            }

            if (path.EndsWith("/api/cloud-storage/status", StringComparison.Ordinal))
            {
                return Json(new List<CloudStorageStatusDto>());
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T payload)
            => new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    }
}
