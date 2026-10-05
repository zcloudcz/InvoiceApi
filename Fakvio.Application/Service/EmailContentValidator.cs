namespace Fakvio.Application.Service;

/// <summary>
/// Validates the caller-supplied subject and full HTML body of an invoice e-mail. This is a trust boundary:
/// the text comes straight from the browser editor or an API / MCP client, so it is checked BEFORE anything
/// is rendered or sent.
/// </summary>
/// <remarks>
/// Junior note: this is a small deny-list, NOT an HTML sanitizer (no sanitizer package is installed). It stops
/// the obvious active content. The ceiling: it does not decode entities or catch every obfuscation
/// (<c>&amp;#106;avascript:</c>, <c>onerror=</c> attributes, ...). That is acceptable because mail clients
/// strip active content themselves and the sender is an authenticated user mailing their own client; upgrade to
/// a real sanitizer (e.g. HtmlSanitizer) if bodies from untrusted parties are ever accepted.
/// </remarks>
public static class EmailContentValidator
{
    /// <summary>Largest accepted HTML body, in characters (256 KB of text; far above any real invoice mail).</summary>
    public const int MaxHtmlBodyLength = 256 * 1024;

    /// <summary>Largest accepted subject (matches the DTO attribute).</summary>
    public const int MaxSubjectLength = 500;

    /// <summary>Markers that are never allowed in an e-mail body (matched case-insensitively).</summary>
    private static readonly string[] ForbiddenMarkers = ["<script", "<iframe", "<object", "<embed", "javascript:"];

    /// <summary>
    /// Returns a message that is safe to show to the caller when the subject or body is not acceptable,
    /// or null when both are fine (null / empty is fine - both are optional overrides).
    /// </summary>
    public static string? Validate(string? subject, string? htmlBody)
    {
        if (subject is not null)
        {
            if (subject.Length > MaxSubjectLength)
                return $"Subject is too long (max {MaxSubjectLength} characters).";

            // CR/LF would let a caller inject extra mail headers.
            if (subject.Contains('\r') || subject.Contains('\n'))
                return "Subject must be a single line (no line breaks).";
        }

        if (htmlBody is null)
            return null;

        if (htmlBody.Length > MaxHtmlBodyLength)
            return $"E-mail body is too large (max {MaxHtmlBodyLength / 1024} KB).";

        foreach (var marker in ForbiddenMarkers)
        {
            if (htmlBody.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return $"E-mail body must not contain active content ('{marker}').";
        }

        return null;
    }
}
