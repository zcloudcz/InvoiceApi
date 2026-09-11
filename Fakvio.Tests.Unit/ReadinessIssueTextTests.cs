using System.Globalization;
using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Tests for <see cref="ReadinessIssueText.DescribeMissingFields"/> against the REAL resource
/// files.
///
/// Why not the key-echoing localizer the banner tests use: the reported bug was a user reading
/// "CreditNote" under a Czech sentence. An echo localizer turns every key into itself, so it
/// cannot tell "translated" from "leaked raw" — only the real .resx can.
/// </summary>
public class ReadinessIssueTextTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IStringLocalizer<SharedResource> _localizer;

    // The culture is a thread-level setting and xUnit reuses threads across test classes;
    // leaving en-US behind made an unrelated culture-sensitive test fail. Restored in Dispose.
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

    public ReadinessIssueTextTests()
    {
        // Same minimal wiring as SharedResourceLocalizationTests — the app's AddLocalization call.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        _serviceProvider = services.BuildServiceProvider();
        _localizer = _serviceProvider.GetRequiredService<IStringLocalizer<SharedResource>>();

        CultureInfo.CurrentUICulture = new CultureInfo("cs-CZ");
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _originalUiCulture;
        _serviceProvider.Dispose();
    }

    [Fact]
    public void MissingCreditNoteSequence_IsNamedInCzech_NotAsTheEnumName()
    {
        // The exact report from production: NUMBER_SEQUENCE_MISSING with ["CreditNote"].
        var issue = Issue(ReadinessCodes.NumberSequenceMissing, nameof(EDocumentType.CreditNote));

        ReadinessIssueText.DescribeMissingFields(_localizer, issue).ShouldBe("Dobropis");
    }

    [Fact]
    public void MissingSequence_UsesTheUserCulture()
    {
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");
        var issue = Issue(ReadinessCodes.NumberSequenceMissing, nameof(EDocumentType.CreditNote));

        ReadinessIssueText.DescribeMissingFields(_localizer, issue).ShouldBe("Credit Note");
    }

    [Fact]
    public void IssuerFields_AreTranslatedAndKeepTheirOrder()
    {
        // Every other rule lists property names — a different key family than document types.
        var issue = Issue(ReadinessCodes.IssuerAddressIncomplete, "Street", "PostalCode");

        ReadinessIssueText.DescribeMissingFields(_localizer, issue).ShouldBe("Ulice, PSČ");
    }

    [Fact]
    public void UnknownField_FallsBackToItsRawName_InsteadOfDisappearing()
    {
        // Newer API than UI: a field without a label is still better shown than dropped.
        var issue = Issue(ReadinessCodes.IssuerAddressIncomplete, "Street", "SomeFutureField");

        ReadinessIssueText.DescribeMissingFields(_localizer, issue).ShouldBe("Ulice, SomeFutureField");
    }

    [Fact]
    public void IssueWithoutFields_DescribesAsEmpty()
    {
        ReadinessIssueText.DescribeMissingFields(_localizer, Issue(ReadinessCodes.IssuerMissing))
            .ShouldBeEmpty();
    }

    private static ReadinessIssueDto Issue(string code, params string[] fields)
        => new() { Code = code, Severity = EReadinessSeverity.Blocking, MissingFields = [.. fields] };
}
