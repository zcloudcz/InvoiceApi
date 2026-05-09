using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.FileAttachment;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that allows the AI assistant to attach a file to an entity (Invoice, ReceivedInvoice, Client).
///
/// The AI model receives file content as a base64-encoded string (the user pastes or drops a file
/// in the chat UI which the frontend encodes to base64 before sending). This tool decodes the bytes
/// and delegates to IFileAttachmentService.UploadAsync — the same service used by the normal
/// file attachment UI, so all storage, size validation, and metadata rules apply identically.
///
/// Typical usage:
///   "Přilož fakturu contract.pdf k faktuře 42"
///   "Attach file report.pdf to invoice 42"
///   "Upload document to client 7"
///
/// Parameters:
///   - entity_name (required): "Invoice", "ReceivedInvoice", or "Client"
///   - record_id (required): primary key of the target entity record
///   - file_name (required): original file name (e.g. "contract.pdf")
///   - file_content_base64 (required): file bytes encoded as Base64
///   - content_type (optional, default: "application/octet-stream"): MIME type
///   - description (optional): human-readable note about the attachment
///
/// Why base64?
/// AI model responses and tool parameters are JSON strings. Binary data must be encoded
/// as text for transport. The frontend encodes the file bytes to base64 before sending
/// the message; this tool decodes them back to a byte array.
/// </summary>
public class AttachFileTool : IChatTool
{
    // Valid entity names accepted by this tool — must match EntityName used by IFileAttachmentService.
    private static readonly IReadOnlySet<string> AllowedEntities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Invoice",
            "ReceivedInvoice",
            "Client"
        };

    private readonly IFileAttachmentService _fileAttachmentService;
    private readonly ILogger<AttachFileTool> _logger;

    public AttachFileTool(
        IFileAttachmentService fileAttachmentService,
        ILogger<AttachFileTool> logger)
    {
        _fileAttachmentService = fileAttachmentService;
        _logger = logger;
    }

    public string ToolName => "attach_file";

    public string Description =>
        "Attach a file to an entity (Invoice, ReceivedInvoice, or Client). " +
        "The file content must be provided as a Base64-encoded string. " +
        "Returns the attachment ID, file name, and size upon success.";

    public string ParameterDescription =>
        "entity_name (string, required): target entity type — Invoice, ReceivedInvoice, or Client. " +
        "record_id (string, required): primary key of the target entity record (numeric). " +
        "file_name (string, required): original file name including extension (e.g. contract.pdf). " +
        "file_content_base64 (string, required): file bytes encoded as Base64. " +
        "content_type (string, optional, default application/octet-stream): MIME type of the file. " +
        "description (string, optional): human-readable note about the attachment.";

    /// <summary>
    /// Decodes the base64 file content and uploads the file via IFileAttachmentService.
    /// Returns a success message with attachment metadata on success,
    /// or a descriptive failure message if any parameter is invalid.
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // ── Parameter extraction ──────────────────────────────────────────────

        parameters.TryGetValue("entity_name", out var entityName);
        parameters.TryGetValue("record_id", out var recordIdStr);
        parameters.TryGetValue("file_name", out var fileName);
        parameters.TryGetValue("file_content_base64", out var base64Content);
        parameters.TryGetValue("content_type", out var contentType);
        parameters.TryGetValue("description", out var description);

        // ── Validation ────────────────────────────────────────────────────────

        if (string.IsNullOrWhiteSpace(entityName))
            return ChatToolResult.Failure(
                "Parameter 'entity_name' is required. Valid values: Invoice, ReceivedInvoice, Client.");

        if (!AllowedEntities.Contains(entityName))
            return ChatToolResult.Failure(
                $"Invalid entity_name '{entityName}'. Valid values: Invoice, ReceivedInvoice, Client.");

        if (string.IsNullOrWhiteSpace(recordIdStr) || !long.TryParse(recordIdStr.Trim(), out var recordId))
            return ChatToolResult.Failure(
                "Parameter 'record_id' is required and must be a numeric entity ID.");

        if (string.IsNullOrWhiteSpace(fileName))
            return ChatToolResult.Failure("Parameter 'file_name' is required.");

        if (string.IsNullOrWhiteSpace(base64Content))
            return ChatToolResult.Failure(
                "Parameter 'file_content_base64' is required. Encode the file bytes as Base64.");

        // ── Base64 decode ─────────────────────────────────────────────────────

        byte[] fileBytes;
        try
        {
            // Trim whitespace — some encoders insert newlines every 76 chars.
            fileBytes = Convert.FromBase64String(base64Content.Trim());
        }
        catch (FormatException)
        {
            return ChatToolResult.Failure(
                "Parameter 'file_content_base64' is not valid Base64. " +
                "Ensure the file content is properly encoded.");
        }

        if (fileBytes.Length == 0)
            return ChatToolResult.Failure("The decoded file is empty. Provide a non-empty file.");

        // Normalise entity name capitalisation so the DB stores consistent values.
        // E.g., the AI might send "invoice" — we normalise to "Invoice".
        var normalisedEntityName = AllowedEntities
            .First(e => e.Equals(entityName, StringComparison.OrdinalIgnoreCase));

        // Default MIME type when the AI does not know the content type.
        var resolvedContentType = string.IsNullOrWhiteSpace(contentType)
            ? "application/octet-stream"
            : contentType.Trim();

        _logger.LogInformation(
            "AttachFileTool: entity={Entity}, recordId={RecordId}, fileName={FileName}, " +
            "size={Bytes} bytes, contentType={ContentType}",
            normalisedEntityName, recordId, fileName, fileBytes.Length, resolvedContentType);

        // ── Upload via service ────────────────────────────────────────────────

        var upload = new FileAttachmentUploadDto
        {
            EntityName = normalisedEntityName,
            RecordId = recordId,
            FileName = fileName.Trim(),
            ContentType = resolvedContentType,
            FileContent = fileBytes,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()
        };

        FileAttachmentDto attachment;
        try
        {
            attachment = await _fileAttachmentService.UploadAsync(upload, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "AttachFileTool: upload failed for {Entity}/{RecordId}/{FileName}",
                normalisedEntityName, recordId, fileName);
            return ChatToolResult.Failure($"File upload failed: {ex.Message}");
        }

        // ── Format success response ────────────────────────────────────────────

        var sizeMb = attachment.FileSizeBytes / (1024.0 * 1024.0);
        var sizeDisplay = sizeMb >= 1
            ? $"{sizeMb:N1} MB"
            : $"{attachment.FileSizeBytes / 1024.0:N1} KB";

        var msg = $"File attached successfully.\n" +
                  $"- Attachment ID: {attachment.Id}\n" +
                  $"- File name: {attachment.OriginalFileName}\n" +
                  $"- Size: {sizeDisplay}\n" +
                  $"- Entity: {attachment.EntityName} #{attachment.RecordId}\n" +
                  $"- Content type: {attachment.ContentType}";

        if (!string.IsNullOrEmpty(attachment.Description))
            msg += $"\n- Description: {attachment.Description}";

        return ChatToolResult.Success(msg);
    }
}
