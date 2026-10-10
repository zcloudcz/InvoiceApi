using System.Text.Json;
using Fakvio.Contracts.Dto.DownloadLink;

namespace Fakvio.McpServer.Tools;

/// <summary>
/// Shapes the JSON every file-producing tool returns: a short-lived download link, never the file bytes.
///
/// Junior note: AI clients cannot turn base64 text into a file, and a 100 kB PDF would burn ~130k characters
/// of the conversation. The API therefore mints a signed URL (<c>POST /api/download-links</c>) that the user
/// opens in a browser. This class is not a tool itself (no [McpServerToolType]) - the SDK ignores it.
/// </summary>
internal static class DownloadLinkResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="link">Link returned by the API.</param>
    /// <param name="mimeType">Content type of the file behind the link; null when only the server knows it (stored attachments).</param>
    /// <param name="fileName">Name of the file when it is known up front; null otherwise (the server names it on download).</param>
    /// <remarks>
    /// <c>skippedDocuments</c> is written only when the API reported it (accounting export): the number of
    /// documents missing from the file because the target system cannot represent them.
    /// </remarks>
    public static string ToJson(DownloadLinkDto link, string? mimeType, string? fileName = null)
        => JsonSerializer.Serialize(new
        {
            success = true,
            downloadUrl = link.Url,
            expiresAt = link.ExpiresAt,
            mimeType,
            fileName,
            skippedDocuments = link.SkippedDocuments,
            note = "Give this link to the user to open in a browser. Do not fetch or load the file contents into the conversation. " +
                   "The link works without sign-in until it expires; request a new one if it has."
        }, JsonOptions);
}
