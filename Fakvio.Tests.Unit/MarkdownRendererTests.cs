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

    // ─── Angle-bracket autolinks ─────────────────────────────────────────────
    // CommonMark has a second link syntax: <scheme:rest> renders as a live anchor.
    // Markdig parses it into AutolinkInline, a *different* node type than the
    // [text](url) form (LinkInline) — so it needs its own sanitisation pass.

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    public void ToSafeHtml_DangerousAngleBracketAutolink_IsNotALink(string url)
    {
        var html = MarkdownRenderer.ToSafeHtml($"<{url}>");

        // The URL stays readable as plain text, but there is no anchor to click.
        html.ShouldNotContain("<a ");
        html.ShouldNotContain("href");
        html.ShouldContain(url);
    }

    [Fact]
    public void ToSafeHtml_DangerousAutolinkInsideBlockquote_IsNotALink()
    {
        // Regression guard: the sanitisation must walk the whole document tree,
        // not just top-level paragraphs.
        var markdown = """
            > Poznámka: <javascript:alert(1)>
            """;

        var html = MarkdownRenderer.ToSafeHtml(markdown);

        html.ShouldContain("<blockquote>");
        html.ShouldNotContain("<a ");
        html.ShouldNotContain("href");
    }

    [Fact]
    public void ToSafeHtml_DangerousAutolinkInsideTableCell_IsNotALink()
    {
        // Table cells are the deepest container a model routinely produces (an
        // answer to "list my invoices" is a pipe table), and pipe tables come from
        // an extension rather than CommonMark core — so they get their own guard
        // instead of relying on the blockquote case above.
        var markdown = """
            | Doklad | Odkaz                |
            |--------|----------------------|
            | FAK-1  | <javascript:alert(1)> |
            """;

        var html = MarkdownRenderer.ToSafeHtml(markdown);

        html.ShouldContain("<table");
        html.ShouldContain("javascript:alert(1)"); // survives as inert plain text
        html.ShouldNotContain("<a ");
        html.ShouldNotContain("href");
    }

    [Fact]
    public void ToSafeHtml_AdjacentAutolinks_NeutralisesEveryHostileOne_AndKeepsTheBenignOne()
    {
        // The autolink pass mutates the inline tree while it walks it, so the pass must
        // survive its own edits: sanitising one node may not make the renderer lose the
        // siblings that follow. A benign link in the middle is what makes that failure
        // mode dangerous — the visible half of the output still looks right while the
        // trailing vbscript: autolink reaches the DOM as a live anchor.
        //
        // Note on MarkdownRenderer's ToList(): with Markdig 1.3.2 removing it does NOT
        // change the output (measured across paragraphs, blockquotes, lists, emphasis
        // and table cells), so this test is not a guard on that call. It pins the
        // observable contract instead, which is what would break if a future Markdig
        // walker stopped tolerating mutation.
        var markdown = "<javascript:alert(1)> <https://ares.gov.cz> <vbscript:msgbox(1)>";

        var html = MarkdownRenderer.ToSafeHtml(markdown);

        html.ShouldNotContain("href=\"javascript:", Case.Insensitive);
        html.ShouldNotContain("href=\"vbscript:", Case.Insensitive);
        html.ShouldContain("href=\"https://ares.gov.cz\"");
        CountAnchors(html).ShouldBe(1, "only the https autolink may stay clickable");

        // Both rejected URLs degrade to readable text rather than disappearing.
        html.ShouldContain("javascript:alert(1)");
        html.ShouldContain("vbscript:msgbox(1)");
    }

    [Fact]
    public void ToSafeHtml_HttpsAngleBracketAutolink_StaysLive()
    {
        var html = MarkdownRenderer.ToSafeHtml("Zdroj: <https://ares.gov.cz>");

        html.ShouldContain("href=\"https://ares.gov.cz\"");
    }

    [Fact]
    public void ToSafeHtml_EmailAngleBracketAutolink_StaysLive()
    {
        // Markdig renders e-mail autolinks with an implicit "mailto:" prefix — the
        // raw Url has no scheme at all, so it must not be judged by IsSafeUrl.
        var html = MarkdownRenderer.ToSafeHtml("<ucetni@example.com>");

        html.ShouldContain("href=\"mailto:ucetni@example.com\"");
    }

    [Fact]
    public void ToSafeHtml_PlainTextWithAngleBrackets_IsEscaped()
    {
        var html = MarkdownRenderer.ToSafeHtml("Podmínka: a < b && b > c");

        html.ShouldContain("&lt;");
        html.ShouldContain("&gt;");
        html.ShouldContain("&amp;&amp;");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Number of opening anchor tags in rendered HTML.</summary>
    private static int CountAnchors(string html) => html.Split("<a ").Length - 1;
}
