using Bunit;
using Fakvio.Contracts.Dto.CompanySettings;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit render tests for EpoSettingsSection — the EPO header editor hosted on
/// /my-company (issue #158).
///
/// Before the fix there was no UI anywhere for EpoTaxOfficeCode &amp; co., so the
/// "fill in the missing fields" link on /vat-report led to a page that could not
/// fix anything. These tests pin down that the section really renders the fields
/// and hands the entered values back as a partial update DTO.
/// </summary>
public class EpoSettingsSectionTests : BunitContext, IAsyncLifetime
{
    // MudBlazor's PopoverService only supports async disposal; xunit disposes test
    // classes synchronously, so route disposal through IAsyncLifetime.
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public EpoSettingsSectionTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Localizer returns the key itself — good enough for asserting which
        // labels are rendered, and it keeps the tests independent of translations.
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(
            ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);
    }

    /// <summary>Settings record with every EPO field filled in.</summary>
    private static CompanySystemSettingsDto ExistingSettings() => new()
    {
        CompanyId = 42,
        EpoTaxOfficeCode = 451,
        EpoTaxOfficeBranchCode = 2001,
        EpoContactPhone = "+420123456789",
        EpoContactEmail = "ucetni@example.cz",
        EpoAuthorizedPersonName = "Jan Novák"
    };

    private IRenderedComponent<EpoSettingsSection> RenderSection(
        CompanySystemSettingsDto? settings,
        EventCallback<UpdateCompanySystemSettingsDto> onSave = default)
        => Render<EpoSettingsSection>(ps => ps
            .Add(p => p.Settings, settings)
            .Add(p => p.OnSave, onSave));

    [Fact]
    public void RendersAllEpoHeaderFields()
    {
        var cut = RenderSection(settings: null);

        // The two fields the API validates plus the three optional contact fields.
        var labels = cut.FindAll("label").Select(l => l.TextContent).ToList();
        labels.ShouldContain(l => l.Contains("Epo_TaxOfficeCode"));
        labels.ShouldContain(l => l.Contains("Epo_TaxOfficeBranchCode"));
        labels.ShouldContain(l => l.Contains("Epo_ContactPhone"));
        labels.ShouldContain(l => l.Contains("Epo_ContactEmail"));
        labels.ShouldContain(l => l.Contains("Epo_AuthorizedPersonName"));
    }

    [Fact]
    public void PrefillsInputs_FromExistingSettings()
    {
        var cut = RenderSection(ExistingSettings());

        var values = cut.FindAll("input").Select(i => i.GetAttribute("value")).ToList();
        values.ShouldContain("451");
        values.ShouldContain("2001");
        values.ShouldContain("+420123456789");
        values.ShouldContain("ucetni@example.cz");
        values.ShouldContain("Jan Novák");
    }

    [Fact]
    public void Save_EmitsDto_WithTheEnteredValues()
    {
        UpdateCompanySystemSettingsDto? saved = null;
        var callback = EventCallback.Factory.Create<UpdateCompanySystemSettingsDto>(
            this, dto => saved = dto);

        var cut = RenderSection(ExistingSettings(), callback);
        // Overwrite the tax office code the way a user would.
        cut.FindAll("input")[0].Change("999");

        ClickSave(cut);

        saved.ShouldNotBeNull();
        saved.EpoTaxOfficeCode.ShouldBe(999);
        saved.EpoTaxOfficeBranchCode.ShouldBe(2001);
        saved.EpoContactEmail.ShouldBe("ucetni@example.cz");
    }

    [Fact]
    public void Save_TouchesOnlyEpoFields_SoSmtpAndAiSurviveThePartialUpdate()
    {
        UpdateCompanySystemSettingsDto? saved = null;
        var callback = EventCallback.Factory.Create<UpdateCompanySystemSettingsDto>(
            this, dto => saved = dto);

        var cut = RenderSection(ExistingSettings(), callback);
        ClickSave(cut);

        // The API keeps every field left null — so the SMTP / AI settings stored on the
        // same CompanySystemSettings record must not be part of this DTO.
        saved.ShouldNotBeNull();
        saved.SmtpHost.ShouldBeNull();
        saved.SmtpPassword.ShouldBeNull();
        saved.AiClaudeApiKey.ShouldBeNull();
        saved.AiDefaultProvider.ShouldBeNull();
    }

    [Fact]
    public void Save_SendsEmptyString_ForClearedTextField_SoItCanBeCleared()
    {
        UpdateCompanySystemSettingsDto? saved = null;
        var callback = EventCallback.Factory.Create<UpdateCompanySystemSettingsDto>(
            this, dto => saved = dto);

        var cut = RenderSection(ExistingSettings(), callback);
        // Clear the contact phone (3rd input — after the two numeric codes).
        cut.FindAll("input")[2].Change("");

        ClickSave(cut);

        // null would mean "keep the stored value" for the API, which would make
        // clearing a field impossible — the section must send an empty string.
        saved.ShouldNotBeNull();
        saved.EpoContactPhone.ShouldBe(string.Empty);
    }

    [Fact]
    public void ShowsProgressBarInsteadOfForm_WhileLoading()
    {
        var cut = Render<EpoSettingsSection>(ps => ps.Add(p => p.Loading, true));

        cut.FindAll("input").ShouldBeEmpty();
        cut.FindAll(".mud-progress-linear").ShouldNotBeEmpty();
    }

    /// <summary>
    /// Clicks the Save button. Located by its label because MudNumericField renders
    /// its own spinner buttons, so "the first button" is not the Save button.
    /// </summary>
    private static void ClickSave(IRenderedComponent<EpoSettingsSection> cut)
        => cut.FindAll("button").First(b => b.TextContent.Contains("Btn_Save")).Click();
}
