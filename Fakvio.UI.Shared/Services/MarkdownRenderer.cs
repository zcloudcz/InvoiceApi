using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Fakvio.UI.Shared.Services;

/// <summary>
/// Converts markdown (as produced by the AI assistant) into HTML that is safe to
/// inject into the DOM via <c>MarkupString</c>.
///
/// Why a separate static helper instead of doing this inside the Razor component?
/// The sanitisation rules are the security-relevant part and must be unit-testable
/// without rendering a component. The component (<c>MarkdownView.razor</c>) is then
/// a two-line wrapper around this class.
///
/// SECURITY — the model output is untrusted input (it can be steered by the content
/// of an invoice, an e-mail or a PDF the user attached). Two defences are applied:
/// 1. <see cref="MarkdownPipelineBuilder.DisableHtml"/> — raw HTML in the markdown is
///    escaped and rendered as text instead of being passed through. That alone kills
///    &lt;script&gt;, &lt;img onerror&gt; and friends.
/// 2. Link destinations are whitelisted (see <see cref="IsSafeUrl"/>) — Markdig itself
///    happily emits <c>&lt;a href="javascript:..."&gt;</c>, which would be a one-click XSS.
/// </summary>
public static class MarkdownRenderer
{
    /// <summary>
    /// URL schemes a link (or image) may point at. Everything else — most importantly
    /// <c>javascript:</c> and <c>data:</c> — is replaced by a harmless placeholder.
    /// </summary>
    private static readonly string[] AllowedSchemes = ["http://", "https://", "mailto:"];

    /// <summary>
    /// Destination used for links whose original URL was rejected. "#" keeps the link
    /// text visible (so the user still sees what the model wrote) but makes it inert.
    /// </summary>
    private const string BlockedUrlPlaceholder = "#";

    /// <summary>
    /// Markdown feature set. Deliberately NOT <c>UseAdvancedExtensions()</c>: that bundle
    /// includes generic attributes (<c>{...}</c>), which let the markdown author set
    /// arbitrary HTML attributes — an XSS vector with untrusted content. Only the
    /// extensions an AI answer actually needs are enabled.
    /// Headings, lists, code blocks, quotes and links are CommonMark core, no extension needed.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()      // | a | b | tables — models emit these constantly
        .UseEmphasisExtras()  // ~~strikethrough~~, ++inserted++
        .UseAutoLinks()       // bare https://… becomes a link
        .DisableHtml()        // raw HTML is escaped, not passed through
        .Build();

    /// <summary>
    /// Renders markdown to sanitised HTML. Returns an empty string for null/blank input.
    /// </summary>
    public static string ToSafeHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        // Parse first so the link destinations can be inspected before rendering.
        var document = Markdig.Markdown.Parse(markdown, Pipeline);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url))
                link.Url = BlockedUrlPlaceholder;
        }

        // CommonMark's second link syntax — <scheme:rest> — parses into AutolinkInline,
        // a different node type that the loop above never sees. Without this pass
        // "<javascript:alert(1)>" would reach the DOM as a live anchor.
        //
        // ToList() is mandatory: Descendants() is a lazy tree walk and ReplaceBy()
        // mutates the tree underneath it.
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            // E-mail autolinks (<user@example.com>) carry a bare address in Url and
            // Markdig prefixes "mailto:" only while rendering — judging that raw value
            // by IsSafeUrl would be wrong, and mailto: is whitelisted anyway.
            if (autolink.IsEmail || IsSafeUrl(autolink.Url))
                continue;

            // Unlike LinkInline there is no separate link text to preserve: the anchor
            // text IS the URL. Setting Url = "#" would therefore also erase the text,
            // so the whole node is downgraded to plain text instead. The literal is
            // HTML-escaped on render like any other text content.
            autolink.ReplaceBy(new LiteralInline(autolink.Url));
        }

        return Markdig.Markdown.ToHtml(document, Pipeline);
    }

    /// <summary>
    /// A URL is accepted when it is relative (no scheme, e.g. "/invoices/1" — used by the
    /// assistant to link inside the app) or uses one of <see cref="AllowedSchemes"/>.
    /// Anything else is rejected. Matching is case-insensitive because "JavaScript:" is
    /// just as executable as "javascript:".
    /// </summary>
    private static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var trimmed = url.Trim();

        foreach (var scheme in AllowedSchemes)
        {
            if (trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // No colon at all → no scheme, so the browser resolves it against the current
        // origin. That covers "/invoices/1" but also the protocol-relative "//host/x",
        // which points off-site — harmless here, because http/https links are allowed
        // by design anyway, and no scheme means no script execution.
        // A colon before the first slash, on the other hand, means some other scheme is
        // in play (javascript:, data:, vbscript:, file:, …) → reject.
        var colon = trimmed.IndexOf(':');
        if (colon < 0)
            return true;

        var slash = trimmed.IndexOf('/');
        return slash >= 0 && slash < colon;
    }
}
