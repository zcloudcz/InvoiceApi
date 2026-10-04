using Fakvio.Contracts.Dto.Email;

namespace Fakvio.Application.Service;

/// <summary>
/// Validates and decodes client-supplied extra email attachments (base64 in the JSON body).
/// This is a trust boundary: everything here comes straight from an API / MCP caller, so the
/// checks run BEFORE any large allocation or any SMTP work.
/// </summary>
/// <remarks>
/// Junior note: every failure throws <see cref="ArgumentException"/> with a message that is safe
/// to show to the caller (it never echoes file content). The controller turns it into HTTP 400.
/// </remarks>
public static class EmailAttachmentValidator
{
    /// <summary>Maximum number of extra files per email.</summary>
    public const int MaxFiles = 5;

    /// <summary>Maximum total size of all decoded files (10 MB).</summary>
    public const int MaxTotalBytes = 10 * 1024 * 1024;

    /// <summary>Maximum file name length (matches the DTO attribute and common file-system limits).</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>
    /// Largest base64 text that can still decode to <see cref="MaxTotalBytes"/>: base64 uses 4 characters
    /// per 3 bytes. Checking the string length first means an oversized payload is rejected without
    /// ever allocating its decoded copy.
    /// </summary>
    private const long MaxTotalBase64Chars = ((long)MaxTotalBytes + 2) / 3 * 4;

    /// <summary>
    /// Allowed extensions. A whitelist (not a blacklist) so a new dangerous type is rejected by default;
    /// executables, scripts and .html are deliberately absent.
    /// </summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".isdoc", ".xml", ".txt", ".csv", ".png", ".jpg", ".jpeg", ".xlsx", ".docx", ".zip"
    };

    /// <summary>Human-readable whitelist for error messages and docs.</summary>
    public static string AllowedExtensionsText => string.Join(", ", AllowedExtensions.Order());

    /// <summary>
    /// Validates the list and returns the decoded files. Returns an empty list for null / empty input.
    /// </summary>
    /// <exception cref="ArgumentException">Any rule is violated; nothing should be sent.</exception>
    public static IReadOnlyList<OutgoingEmailAttachment> ValidateAndDecode(IReadOnlyList<EmailAttachmentDto>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
            return [];

        if (attachments.Count > MaxFiles)
            throw new ArgumentException($"At most {MaxFiles} attachments are allowed (got {attachments.Count}).");

        // Cheap pass first: names, extensions and the raw base64 length. Nothing is decoded yet.
        long totalBase64Chars = 0;
        foreach (var item in attachments)
        {
            if (item is null)
                throw new ArgumentException("Attachment entry must not be null.");

            ValidateFileName(item.FileName);

            if (string.IsNullOrWhiteSpace(item.ContentBase64))
                throw new ArgumentException($"Attachment '{item.FileName}' has no content.");

            totalBase64Chars += item.ContentBase64.Length;
            if (totalBase64Chars > MaxTotalBase64Chars)
                throw new ArgumentException(
                    $"Attachments are too large: the total must not exceed {MaxTotalBytes / (1024 * 1024)} MB.");
        }

        // Only now decode - the size is already bounded by the check above.
        var result = new List<OutgoingEmailAttachment>(attachments.Count);
        long totalBytes = 0;
        foreach (var item in attachments)
        {
            byte[] content;
            try
            {
                content = Convert.FromBase64String(item.ContentBase64);
            }
            catch (FormatException)
            {
                throw new ArgumentException($"Attachment '{item.FileName}' is not valid base64.");
            }

            if (content.Length == 0)
                throw new ArgumentException($"Attachment '{item.FileName}' is empty.");

            totalBytes += content.Length;
            if (totalBytes > MaxTotalBytes)
                throw new ArgumentException(
                    $"Attachments are too large: the total must not exceed {MaxTotalBytes / (1024 * 1024)} MB.");

            result.Add(new OutgoingEmailAttachment(item.FileName, content));
        }

        return result;
    }

    private static void ValidateFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Attachment file name is required.");

        if (fileName.Length > MaxFileNameLength)
            throw new ArgumentException($"Attachment file name is longer than {MaxFileNameLength} characters.");

        // Control characters (CR/LF would enable header injection in the MIME part) and invisible
        // Unicode format characters (e.g. U+202E right-to-left override, which makes "x‮fdp.zip"
        // display as "xpiz.pdf" in a mail client and hides the real extension from the recipient).
        if (fileName.Any(c => char.IsControl(c)
                || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            throw new ArgumentException("Attachment file name contains control characters.");

        // Path traversal: the name must be a bare file name. Path.GetFileName only knows the
        // separators of the current OS (a backslash is an ordinary character on Linux), so both
        // are checked explicitly as well.
        if (fileName.Contains('/') || fileName.Contains('\\') || Path.GetFileName(fileName) != fileName)
            throw new ArgumentException($"Attachment file name '{fileName}' must not contain a path.");

        var extension = Path.GetExtension(fileName);
        if (!AllowedExtensions.Contains(extension))
            throw new ArgumentException(
                $"Attachment type '{extension}' is not allowed. Allowed: {AllowedExtensionsText}.");
    }
}
