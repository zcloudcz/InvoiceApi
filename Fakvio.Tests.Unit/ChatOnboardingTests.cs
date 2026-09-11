using Fakvio.Contracts.Dto.Readiness;
using Fakvio.Domain.Enums;
using Fakvio.UI.Shared;
using Fakvio.UI.Shared.Components.Chat;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ChatOnboarding"/> (issue #214).
///
/// This is the decision the conversational onboarding hangs on: an unfinished tenant gets a
/// proactive welcome (and MainLayout opens the chat drawer because of it), a finished one is
/// left alone. Deliberately tested without a live model — the welcome is composed text, not
/// an AI answer, which is exactly what makes the first message of the onboarding testable.
/// </summary>
public class ChatOnboardingTests
{
    private readonly IStringLocalizer<SharedResource> _localizer = Substitute.For<IStringLocalizer<SharedResource>>();

    public ChatOnboardingTests()
    {
        // Echoes the key back, so assertions target keys instead of translations — the same
        // trick ReadinessBannerTests uses.
        _localizer[Arg.Any<string>()].Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
    }

    [Fact]
    public void ReadyTenant_GetsNoWelcome_SoNothingInterruptsThem()
    {
        var welcome = ChatOnboarding.BuildWelcome(new ReadinessReportDto(), _localizer);

        welcome.ShouldBeNull();
    }

    [Fact]
    public void WarningsOnly_GetNoWelcome_BecauseTheUserCanAlreadyInvoice()
    {
        // EPO is the default state of every new tenant, so treating warnings as onboarding
        // material would greet absolutely everyone, forever.
        var report = Report(Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning));

        var welcome = ChatOnboarding.BuildWelcome(report, _localizer);

        welcome.ShouldBeNull();
    }

    [Fact]
    public void UnfinishedTenant_GetsAWelcomeListingEveryBlockingGap()
    {
        var report = Report(
            Issue(ReadinessCodes.IssuerAddressIncomplete, EReadinessSeverity.Blocking),
            Issue(ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking));

        var welcome = ChatOnboarding.BuildWelcome(report, _localizer);

        welcome.ShouldNotBeNull();
        welcome.ShouldContain("Chat_Onboarding_Intro");
        welcome.ShouldContain($"- Readiness_Code_{ReadinessCodes.IssuerAddressIncomplete}");
        welcome.ShouldContain($"- Readiness_Code_{ReadinessCodes.IssuerBankAccountMissing}");
        welcome.ShouldContain("Chat_Onboarding_Ask");
    }

    [Fact]
    public void MixedReport_ListsOnlyTheBlockingGaps()
    {
        var report = Report(
            Issue(ReadinessCodes.IssuerMissing, EReadinessSeverity.Blocking),
            Issue(ReadinessCodes.EpoHeaderIncomplete, EReadinessSeverity.Warning));

        var welcome = ChatOnboarding.BuildWelcome(report, _localizer);

        welcome.ShouldNotBeNull();
        welcome.ShouldContain(ReadinessCodes.IssuerMissing);
        // The EPO warning also carries a SysAdmin-only fix route (issue #345) — one more
        // reason the welcome must not send an ordinary user after it.
        welcome.ShouldNotContain(ReadinessCodes.EpoHeaderIncomplete);
    }

    [Fact]
    public void WelcomeNamesTheIssuer_WhenTheGapBelongsToOneOfSeveralCompanies()
    {
        var issue = Issue(ReadinessCodes.IssuerBankAccountMissing, EReadinessSeverity.Blocking);
        issue.IssuerName = "Druhá firma s.r.o.";

        var welcome = ChatOnboarding.BuildWelcome(Report(issue), _localizer);

        welcome.ShouldNotBeNull();
        welcome.ShouldContain("(Druhá firma s.r.o.)");
    }

    [Fact]
    public void WelcomeNamesTheMissingFields_SoASequenceGapSaysWhichDocumentType()
    {
        // "No sequence for this document type" alone does not say which one — the chat bullet
        // has no caption line like the banner, so the fields ride on the bullet itself.
        var issue = Issue(ReadinessCodes.NumberSequenceMissing, EReadinessSeverity.Blocking);
        issue.MissingFields = [nameof(EDocumentType.CreditNote)];

        var welcome = ChatOnboarding.BuildWelcome(Report(issue), _localizer);

        welcome.ShouldNotBeNull();
        // The echo localizer returns the key, so this proves the document-type label key was used.
        welcome.ShouldContain(
            $"- Readiness_Code_{ReadinessCodes.NumberSequenceMissing} — Template_DocumentTypeCreditNote");
    }

    [Fact]
    public void UnknownCode_FallsBackToTheGenericSentence_InsteadOfLeakingTheCode()
    {
        // Older UI against a newer API. The user must never read "ISSUER_FOO"; shared with
        // the dashboard banner through ReadinessIssueText, so both degrade identically.
        _localizer["Readiness_Code_ISSUER_FOO"]
            .Returns(new LocalizedString("Readiness_Code_ISSUER_FOO", "Readiness_Code_ISSUER_FOO", resourceNotFound: true));

        var welcome = ChatOnboarding.BuildWelcome(
            Report(Issue("ISSUER_FOO", EReadinessSeverity.Blocking)), _localizer);

        welcome.ShouldNotBeNull();
        welcome.ShouldContain("Readiness_Code_Unknown");
        welcome.ShouldNotContain("ISSUER_FOO");
    }

    private static ReadinessReportDto Report(params ReadinessIssueDto[] issues)
        => new() { Issues = [.. issues] };

    private static ReadinessIssueDto Issue(string code, EReadinessSeverity severity)
        => new() { Code = code, Severity = severity, FixRoute = "/my-company" };
}
