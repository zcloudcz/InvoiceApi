using System.Globalization;
using Bunit;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Fakvio.UI.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor;
using MudBlazor.Services;
using Shouldly;

namespace Fakvio.Tests.Unit;

public sealed class UxSharedLocalizationTests : BunitContext, IAsyncLifetime
{
    private readonly CultureInfo _original = CultureInfo.CurrentUICulture;
    public UxSharedLocalizationTests()
    {
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddTransient<MudLocalizer, FakvioMudLocalizer>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        AddAuthorization().SetAuthorized("test@example.test");
    }
    public Task InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() { CultureInfo.CurrentUICulture = _original; await base.DisposeAsync(); }

    [Theory]
    [InlineData("cs-CZ", "Hodnota filtru", "Data dokladu", "Vystavující firma, Kód finančního úřadu")]
    [InlineData("en-US", "Filter value", "Document dates", "Issuing company, Tax office code")]
    public void BuiltInGridAndReadinessFieldsUseSelectedLanguage(string culture, string filter, string dates, string fields)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        var localizer = Services.GetRequiredService<IStringLocalizer<SharedResource>>();
        Services.GetRequiredService<MudLocalizer>()["MudDataGrid_FilterValue"].Value.ShouldBe(filter);
        Services.GetRequiredService<MudLocalizer>()["MudPagination_PageIndex", 2].Value
            .ShouldBe(culture == "cs-CZ" ? "Stránka 2" : "Page 2");
        localizer["Invoice_Dates"].Value.ShouldBe(dates);
        ReadinessIssueText.Fields(localizer, ["IsIssuer", "EpoTaxOfficeCode"]).ShouldBe(fields);
        ReadinessIssueText.Fields(localizer, ["FutureInternalField"]).ShouldNotContain("FutureInternalField");
        Services.GetRequiredService<MudLocalizer>()["MissingFutureMudKey"].ResourceNotFound.ShouldBeTrue();
    }

    [Theory]
    [InlineData("User", "/company-settings", false)]
    [InlineData("Admin", "/company-settings", false)]
    [InlineData("SysAdmin", "/company-settings", true)]
    [InlineData("User", "/number-sequences", false)]
    [InlineData("Admin", "/number-sequences", true)]
    [InlineData("User", "/my-company", true)]
    public void ReadinessLinksOnlyOfferAuthorizedEditors(string role, string route, bool expectedLink)
    {
        AddAuthorization().SetRoles(role);
        var cut = Render<ReadinessFixLink>(p => p.Add(x => x.Route, route));
        cut.WaitForAssertion(() => cut.FindAll("a").Count.ShouldBe(expectedLink ? 1 : 0));
        if (!expectedLink) cut.Markup.ShouldNotBeEmpty();
    }
}
