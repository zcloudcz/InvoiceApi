using Bunit;
using Fakvio.Contracts.Common;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// bUnit tests for FileNameFormatInput: placeholder chips, live preview and the "cleared = empty string"
/// contract with the API (null would mean "not sent, keep the old value").
/// </summary>
public class FileNameFormatInputTests : BunitContext, IAsyncLifetime
{
    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public FileNameFormatInputTests()
    {
        Services.AddMudServices(o => o.PopoverOptions.CheckForPopoverProvider = false);
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Localizer echoes the key, so assertions can target keys instead of translations.
        var localizer = Substitute.For<IStringLocalizer<SharedResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        Services.AddSingleton(localizer);
    }

    private IRenderedComponent<FileNameFormatInput> Render(
        string? value, Action<string?>? onChanged = null, string? language = "cs", string? emptyHint = null) =>
        Render<FileNameFormatInput>(ps => ps
            .Add(p => p.Label, "Label")
            .Add(p => p.Value, value)
            .Add(p => p.Language, language)
            .Add(p => p.DocumentType, EDocumentType.Invoice)
            .Add(p => p.EmptyHint, emptyHint)
            .Add(p => p.ValueChanged, v => onChanged?.Invoke(v)));

    [Fact]
    public void RendersOneChipPerPlaceholderKey()
    {
        var cut = Render("");

        var chips = cut.FindAll(".mud-chip").Select(c => c.TextContent.Trim()).ToList();

        chips.ShouldBe(DocumentFileName.PlaceholderKeys);
    }

    [Fact]
    public void ClickingAChip_AppendsTheMatchingPlaceholder()
    {
        string? changed = null;
        var cut = Render("Faktura_", v => changed = v);

        cut.FindAll(".mud-chip").First(c => c.TextContent.Trim() == "DocumentNumber").Click();

        changed.ShouldBe("Faktura_{{DocumentNumber}}");
    }

    [Fact]
    public void Preview_ShowsTheNameTheServerBuilderProduces()
    {
        var cut = Render("{{DocumentType}}_{{Year}}-{{DocumentNumber}}", language: "en");

        // Same builder as the server, fed with the component's sample data.
        cut.Markup.ShouldContain("Invoice_2026-2026001.pdf");
    }

    [Fact]
    public void UnknownPlaceholder_IsReportedBeforeSaving()
    {
        var cut = Render("{{DocumentNumber}}_{{Typo}}");

        cut.Markup.ShouldContain("FileNameFormat_Unknown");
        cut.Markup.ShouldContain("Typo");
    }

    [Fact]
    public void EmptyValue_ShowsTheEmptyHint_InsteadOfAMisleadingPreview()
    {
        var cut = Render("", emptyHint: "inherit-from-company");

        cut.Markup.ShouldContain("inherit-from-company");
    }

    [Fact]
    public void ClearingTheField_EmitsEmptyString_NotNull()
    {
        string? changed = "unset";
        var cut = Render("abc", v => changed = v);

        cut.Find("input").Input(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = null });

        changed.ShouldBe(""); // null would be read by the API as "not sent, keep"
    }
}
