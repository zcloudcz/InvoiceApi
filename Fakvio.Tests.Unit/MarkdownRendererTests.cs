using Fakvio.UI.Shared.Services;
using Shouldly;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Unit tests for <see cref="MarkdownRenderer"/> — the markdown-to-HTML conversion used
/// for AI assistant answers.
///
/// Two groups of tests:
/// - formatting: the constructs a language model actually emits (bold, lists, tables,
///   code) must survive the conversion,
/// - sanitisation: the model output is untrusted, so raw HTML and dangerous link schemes
///   must never reach the DOM.
/// </summary>
public class MarkdownRendererTests
{
    // ─── Empty input ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToSafeHtml_NullOrBlank_ReturnsEmptyString(string? input)
    {
        MarkdownRenderer.ToSafeHtml(input).ShouldBe(string.Empty);
    }

    // ─── Formatting ──────────────────────────────────────────────────────────

    [Fact]
    public void ToSafeHtml_Bold_RendersStrong()
    {
        var html = MarkdownRenderer.ToSafeHtml("Faktura **FAK-2026-001** je uhrazena.");

        html.ShouldContain("<strong>FAK-2026-001</strong>");
    }

    [Fact]
    public void ToSafeHtml_BulletList_RendersListItems()
    {
        var html = MarkdownRenderer.ToSafeHtml("- první\n- druhá");

        html.ShouldContain("<ul>");
        html.ShouldContain("<li>první</li>");
        html.ShouldContain("<li>druhá</li>");
    }

    [Fact]
    public void ToSafeHtml_PipeTable_RendersTable()
    {
        // Models answer "list my invoices" with a pipe table — the main reason the
        // plain-text rendering looked broken.
        var markdown = """
            | Číslo | Částka |
            |-------|--------|
            | FAK-1 | 1 000  |
            """;

        var html = MarkdownRenderer.ToSafeHtml(markdown);

        html.ShouldContain("<table");
        html.ShouldContain("<th>Číslo</th>");
        html.ShouldContain("<td>FAK-1</td>");
    }

    [Fact]
    public void ToSafeHtml_FencedCodeBlock_RendersPreCode()
    {
        var html = MarkdownRenderer.ToSafeHtml("```\nSELECT 1\n```");

        html.ShouldContain("<pre>");
        html.ShouldContain("<code>");
        html.ShouldContain("SELECT 1");
    }

    [Fact]
    public void ToSafeHtml_Strikethrough_RendersDel()
    {
        // EmphasisExtras extension — not part of CommonMark core.
        var html = MarkdownRenderer.ToSafeHtml("~~zrušeno~~");

        html.ShouldContain("<del>zrušeno</del>");
    }

    [Fact]
    public void ToSafeHtml_RelativeLink_IsKept()
    {
        // The assistant links inside the app; those links must keep working.
        var html = MarkdownRenderer.ToSafeHtml("[detail](/invoices/42)");

        html.ShouldContain("href=\"/invoices/42\"");
    }

    [Fact]
    public void ToSafeHtml_HttpsLink_IsKept()
    {
        var html = MarkdownRenderer.ToSafeHtml("[ARES](https://ares.gov.cz)");

        html.ShouldContain("href=\"https://ares.gov.cz\"");
    }

    // ─── Sanitisation ────────────────────────────────────────────────────────

    [Fact]
    public void ToSafeHtml_RawScriptTag_IsEscaped_NotExecutable()
    {
        var html = MarkdownRenderer.ToSafeHtml("<script>alert('xss')</script>");

        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
    }

    [Fact]
    public void ToSafeHtml_InlineHtmlAttribute_IsEscaped()
    {
        var html = MarkdownRenderer.ToSafeHtml("<img src=x onerror=\"alert(1)\">");

        html.ShouldNotContain("<img");
        html.ShouldContain("&lt;img");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("  javascript:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox(1)")]
    public void ToSafeHtml_DangerousLinkScheme_IsNeutralised(string url)
    {
        var html = MarkdownRenderer.ToSafeHtml($"[klikni]({url})");

        // The link text stays visible, but the destination is inert.
        html.ShouldContain("klikni");
        html.ShouldContain("href=\"#\"");
        html.ShouldNotContain("javascript:", Case.Insensitive);
        html.ShouldNotContain("vbscript:", Case.Insensitive);
        html.ShouldNotContain("data:text/html");
    }

    [Fact]
    public void ToSafeHtml_DangerousImageSource_IsNeutralised()
    {
        var html = MarkdownRenderer.ToSafeHtml("![logo](javascript:alert(1))");

        html.ShouldNotContain("javascript:", Case.Insensitive);
        html.ShouldContain("src=\"#\"");
    }

    [Fact]
    public void ToSafeHtml_PlainTextWithAngleBrackets_IsEscaped()
    {
        var html = MarkdownRenderer.ToSafeHtml("Podmínka: a < b && b > c");

        html.ShouldContain("&lt;");
        html.ShouldContain("&gt;");
        html.ShouldContain("&amp;&amp;");
    }
}
