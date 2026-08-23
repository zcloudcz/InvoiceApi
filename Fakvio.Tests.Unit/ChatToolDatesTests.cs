using System.Globalization;
using Fakvio.Infrastructure.Service.ChatTools;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="ChatToolDates"/> — the date parsing shared by the reporting chat
/// tools (issue #228). The tool-level tests in <see cref="ReportingChatToolTests"/> cover what
/// each tool DOES with a date; these cover the parsing rules themselves, because they are the
/// part that is culture-sensitive and the part a model gets wrong most often.
///
/// Junior note: the helper is <c>internal</c>. The test project can see it because
/// Fakvio.Infrastructure.csproj declares <c>&lt;InternalsVisibleTo Include="Fakvio.Tests.Unit" /&gt;</c>.
/// </summary>
public class ChatToolDatesTests
{
    /// <summary>The parameter name used throughout — any key behaves the same, this one just reads well.</summary>
    private const string DateKey = "issue_date_from";

    /// <summary>The one calendar day every "readable input" case in this class resolves to.</summary>
    private static readonly DateTime ExpectedDay = new(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

    private static Dictionary<string, string> Parameters(string? rawValue)
        => rawValue is null ? [] : new Dictionary<string, string> { [DateKey] = rawValue };

    // ═══════════════════════════════════════════════════════════════════════
    //  TryParse — accepted formats and culture independence
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// All three advertised formats have to work, whitespace included — the model copies the
    /// user's wording, and "  2026-03-15 " is what arrives when it pads a value into a sentence.
    /// The <c>dd/MM/yyyy</c> variant had no coverage at all before this test.
    /// </summary>
    [Theory]
    [InlineData("2026-03-15")]
    [InlineData(" 2026-03-15 ")]
    [InlineData("15.03.2026")]
    [InlineData("15/03/2026")]
    public void TryParse_AdvertisedFormat_YieldsUtcMidnight(string rawValue)
    {
        var parsed = ChatToolDates.TryParse(Parameters(rawValue), DateKey, out var value);

        parsed.ShouldBeTrue();
        value.ShouldBe(ExpectedDay);
        value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    /// <summary>
    /// The reason the helper exists at all (see its class summary): the same input must produce
    /// the same date on every machine. "03/04/2026" is 3 April here, never 4 March.
    ///
    /// Junior note on why <c>th-TH</c> is in the list. Swapping the day and the month is the
    /// culture bug everyone expects, but .NET happens to read all three of our formats the same
    /// way under cs-CZ, en-US and the invariant culture — measured, not assumed. What DOES
    /// change with the culture is the CALENDAR: under Thai culture the year 2026 is a Buddhist
    /// era year, so a culture-sensitive parse silently returns 1483-04-03 and the report is
    /// dated five centuries off. That is the case that makes this test able to fail at all —
    /// drop it and the test stops guarding anything.
    /// </summary>
    [Theory]
    [InlineData("cs-CZ")]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    public void TryParse_IsIndependentOfTheThreadCulture(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            ChatToolDates.TryParse(Parameters("03/04/2026"), DateKey, out var slashDate).ShouldBeTrue();
            ChatToolDates.TryParse(Parameters("2026-03-15"), DateKey, out var isoDate).ShouldBeTrue();

            slashDate.ShouldBe(new DateTime(2026, 4, 3, 0, 0, 0, DateTimeKind.Utc));
            isoDate.ShouldBe(ExpectedDay);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    /// <summary>
    /// CHARACTERIZATION of the gap tracked as issue #271, not an endorsement of it.
    ///
    /// The helper's own header names "15.3.2026" as the motivating example, but
    /// <c>AcceptedFormats</c> only lists the zero-padded <c>dd.MM.yyyy</c>, so a single-digit
    /// day or month is rejected. The failure is loud and recoverable (the tool answers
    /// "Use YYYY-MM-DD" and the model retries), which is why #271 is low priority rather than
    /// a bug in this PR.
    ///
    /// When #271 is fixed this test goes RED — that is its purpose. Flip it to
    /// <c>ShouldBeTrue</c> plus a value assertion then.
    /// </summary>
    [Theory]
    [InlineData("15.3.2026")]
    [InlineData("1.1.2026")]
    [InlineData("15/3/2026")]
    [InlineData("2026-3-15")]
    public void TryParse_SingleDigitDayOrMonth_IsRejected_Issue271(string rawValue)
    {
        var parsed = ChatToolDates.TryParse(Parameters(rawValue), DateKey, out var value);

        parsed.ShouldBeFalse();
        value.ShouldBe(default);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TryParseOptional — the three outcomes an optional filter can have
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Absent or blank means "no date restriction", which is a perfectly good answer for an
    /// optional filter — models routinely send an empty string for a parameter they have no
    /// value for, and failing on that would make the tool unusable.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void TryParseOptional_MissingOrBlank_MeansNoFilter(string? rawValue)
    {
        var succeeded = ChatToolDates.TryParseOptional(Parameters(rawValue), DateKey, out var value, out var error);

        succeeded.ShouldBeTrue();
        value.ShouldBeNull();
        error.ShouldBeNull();
    }

    /// <summary>
    /// A readable value is applied as a filter and never reported as an error at the same time.
    /// </summary>
    [Theory]
    [InlineData("2026-03-15")]
    [InlineData("15.03.2026")]
    [InlineData("15/03/2026")]
    public void TryParseOptional_ReadableDate_YieldsTheFilterAndNoError(string rawValue)
    {
        var succeeded = ChatToolDates.TryParseOptional(Parameters(rawValue), DateKey, out var value, out var error);

        succeeded.ShouldBeTrue();
        value.ShouldBe(ExpectedDay);
        error.ShouldBeNull();
    }

    /// <summary>
    /// Present but unreadable is the dangerous case (review round 1, blocking finding B1):
    /// dropping the filter would widen "za březen" to the whole history and the model would
    /// report that total as the March one. So it must fail, name the offending parameter and
    /// its value, and — the part worth pinning — leave <c>value</c> null, so a caller that
    /// ignores the return value still cannot apply a half-parsed date.
    /// </summary>
    [Theory]
    [InlineData("2026-03")]           // month only — the review's original example
    [InlineData("last monday")]       // natural language, the model's favourite shortcut
    [InlineData("2026-13-01")]        // month 13
    [InlineData("2026-02-30")]        // day that does not exist in that month
    [InlineData("15-03-2026")]        // right parts, wrong separator
    [InlineData("2026/03/15")]        // ISO order with slashes
    public void TryParseOptional_PresentButUnreadable_FailsInsteadOfGuessing(string rawValue)
    {
        var succeeded = ChatToolDates.TryParseOptional(Parameters(rawValue), DateKey, out var value, out var error);

        succeeded.ShouldBeFalse();
        value.ShouldBeNull();
        error.ShouldNotBeNullOrWhiteSpace();
        error.ShouldContain(DateKey);
        error.ShouldContain(rawValue);
        error.ShouldContain("YYYY-MM-DD");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Format
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A missing date must render as visible text. An empty gap after "due: " is an invitation
    /// for the model to fill it in with something plausible.
    /// </summary>
    [Fact]
    public void Format_NullDate_RendersAnExplicitPlaceholder()
        => ChatToolDates.Format(null).ShouldBe("(none)");

    /// <summary>
    /// The rendered date is ISO regardless of culture — the model is told dates are YYYY-MM-DD,
    /// and reading back "15.03.2026" from our own output would teach it otherwise. Same
    /// calendar caveat as <see cref="TryParse_IsIndependentOfTheThreadCulture"/>: <c>th-TH</c>
    /// is the case that catches a culture-sensitive format string.
    /// </summary>
    [Theory]
    [InlineData("cs-CZ")]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    public void Format_Date_IsIsoInEveryCulture(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);

            ChatToolDates.Format(ExpectedDay).ShouldBe("2026-03-15");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
