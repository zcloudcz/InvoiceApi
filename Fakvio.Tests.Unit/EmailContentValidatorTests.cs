using Fakvio.Application.Service;
using Fakvio.UI.Shared.Components.Shared;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>Trust-boundary checks for the caller-supplied subject / full HTML body of an invoice e-mail.</summary>
public class EmailContentValidatorTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("Invoice INV1", "<p>Hello <strong>world</strong></p>")]
    [InlineData("", "")]
    public void Validate_NormalContent_IsAccepted(string? subject, string? body) =>
        EmailContentValidator.Validate(subject, body).ShouldBeNull();

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<SCRIPT src=x>")]
    [InlineData("<p>x</p><iframe src='https://evil'></iframe>")]
    [InlineData("<Object data=x>")]
    [InlineData("<embed src=x>")]
    [InlineData("<a href=\"javascript:alert(1)\">click</a>")]
    [InlineData("<a href=\"JavaScript:alert(1)\">click</a>")]
    public void Validate_ActiveContent_IsRejected(string body) =>
        EmailContentValidator.Validate(null, body).ShouldNotBeNull().ShouldContain("active content");

    [Fact]
    public void Validate_BodyAtTheLimit_IsAccepted_AndOneOverIsRejected()
    {
        EmailContentValidator.Validate(null, new string('a', EmailContentValidator.MaxHtmlBodyLength)).ShouldBeNull();
        EmailContentValidator.Validate(null, new string('a', EmailContentValidator.MaxHtmlBodyLength + 1))
            .ShouldNotBeNull().ShouldContain("256 KB");
    }

    [Theory]
    [InlineData("Line one\nBcc: evil@x.cz")]
    [InlineData("Line one\r\nBcc: evil@x.cz")]
    [InlineData("Carriage\rreturn")]
    public void Validate_SubjectWithLineBreak_IsRejected(string subject) =>
        EmailContentValidator.Validate(subject, null).ShouldNotBeNull().ShouldContain("single line");

    [Fact]
    public void Validate_SubjectTooLong_IsRejected() =>
        EmailContentValidator.Validate(new string('s', EmailContentValidator.MaxSubjectLength + 1), null)
            .ShouldNotBeNull().ShouldContain("too long");
}

/// <summary>
/// The rule that decides whether the send dialog posts the editor's HTML or null. The editor re-formats the
/// template on load, so the first reported value is the baseline, not an edit.
/// </summary>
public class EditorEditTrackerTests
{
    [Fact]
    public void NothingReportedYet_IsNotEdited()
    {
        var tracker = new EditorEditTracker();

        tracker.IsEdited.ShouldBeFalse();
        tracker.Current.ShouldBeNull();
    }

    [Fact]
    public void FirstReport_IsTheBaseline_NotAnEdit()
    {
        var tracker = new EditorEditTracker();

        tracker.OnChanged("<p>normalised</p>");

        tracker.IsEdited.ShouldBeFalse();
    }

    [Fact]
    public void ADifferentLaterValue_IsAnEdit_AndTypingItBackIsNot()
    {
        var tracker = new EditorEditTracker();
        tracker.OnChanged("<p>normalised</p>");

        tracker.OnChanged("<p>normalised and more</p>");
        tracker.IsEdited.ShouldBeTrue();
        tracker.Current.ShouldBe("<p>normalised and more</p>");

        tracker.OnChanged("<p>normalised</p>");
        tracker.IsEdited.ShouldBeFalse();
    }

    [Fact]
    public void Reset_ForgetsTheEdit_AndTheEditorsNormalisedEchoKeepsItClean()
    {
        var tracker = new EditorEditTracker();
        tracker.OnChanged("<p>base</p>");
        tracker.OnChanged("<p>edited</p>");

        tracker.Reset();
        tracker.IsEdited.ShouldBeFalse();

        tracker.OnChanged("<p>base</p>"); // the editor echoes the text we pushed back in
        tracker.IsEdited.ShouldBeFalse();
    }
}
