using System.Globalization;
using System.Text;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service.ChatTools;

/// <summary>
/// Chat tool that lists all file attachments for a given entity record.
///
/// Typical usage:
///   "Jaké soubory jsou přiložené k faktuře 42?"
///   "List attachments for invoice 42"
///   "Show files attached to client 7"
///
/// Parameters:
///   - entity_name (required): "Invoice", "ReceivedInvoice", or "Client"
///   - record_id (required): primary key of the target entity record
///
/// Returns a formatted list of attachments (name, size, date, description).
/// Returns an informational message when the entity has no attachments.
/// </summary>
public class ListAttachmentsTool : IChatTool
{
    // Valid entity names — same set as AttachFileTool to ensure consistency.
    private static readonly IReadOnlySet<string> AllowedEntities =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Invoice",
            "ReceivedInvoice",
            "Client"
        };

    private readonly IFileAttachmentService _fileAttachmentService;
    private readonly ILogger<ListAttachmentsTool> _logger;

    public ListAttachmentsTool(
        IFileAttachmentService fileAttachmentService,
        ILogger<ListAttachmentsTool> logger)
    {
        _fileAttachmentService = fileAttachmentService;
        _logger = logger;
    }

    public string ToolName => "list_attachments";

    public string Description =>
        "List all file attachments for an entity record (Invoice, ReceivedInvoice, or Client). " +
        "Returns file name, size, upload date, and optional description for each attachment.";

    public string ParameterDescription =>
        "entity_name (string, required): target entity type — Invoice, ReceivedInvoice, or Client. " +
        "record_id (string, required): primary key of the target entity record (numeric).";

    /// <summary>
    /// Fetches the attachment list from IFileAttachmentService and formats it as a readable text block.
    /// Results are ordered newest-first (handled by the service layer).
    /// </summary>
    public async Task<ChatToolResult> ExecuteAsync(
        Dictionary<string, string> parameters,
        CancellationToken ct = default)
    {
        // ── Parameter extraction and validation ───────────────────────────────

        parameters.TryGetValue("entity_name", out var entityName);
        parameters.TryGetValue("record_id", out var recordIdStr);

        if (string.IsNullOrWhiteSpace(entityName))
            return ChatToolResult.Failure(
                "Parameter 'entity_name' is required. Valid values: Invoice, ReceivedInvoice, Client.");

        if (!AllowedEntities.Contains(entityName))
            return ChatToolResult.Failure(
                $"Invalid entity_name '{entityName}'. Valid values: Invoice, ReceivedInvoice, Client.");

        if (string.IsNullOrWhiteSpace(recordIdStr) || !long.TryParse(recordIdStr.Trim(), out var recordId))
            return ChatToolResult.Failure(
                "Parameter 'record_id' is required and must be a numeric entity ID.");

        // Normalise capitalisation (e.g., "invoice" → "Invoice").
        var normalisedEntityName = AllowedEntities
            .First(e => e.Equals(entityName, StringComparison.OrdinalIgnoreCase));

        _logger.LogInformation(
            "ListAttachmentsTool: entity={Entity}, recordId={RecordId}",
            normalisedEntityName, recordId);

        // ── Fetch attachments ──────────────────────────────────────────────────

        List<Contracts.Dto.FileAttachment.FileAttachmentDto> attachments;
        try
        {
            attachments = await _fileAttachmentService.GetByEntityAsync(normalisedEntityName, recordId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ListAttachmentsTool: failed to list attachments for {Entity}/{RecordId}",
                normalisedEntityName, recordId);
            return ChatToolResult.Failure($"Failed to retrieve attachments: {ex.Message}");
        }

        // ── Format results ────────────────────────────────────────────────────

        if (attachments.Count == 0)
        {
            return ChatToolResult.Success(
                $"No attachments found for {normalisedEntityName} #{recordId}.");
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Attachments for {normalisedEntityName} #{recordId} ({attachments.Count} file(s)):");
        sb.AppendLine();

        foreach (var att in attachments)
        {
            var sizeMb = att.FileSizeBytes / (1024.0 * 1024.0);
            var sizeDisplay = sizeMb >= 1
                ? $"{sizeMb:N1} MB"
                : $"{att.FileSizeBytes / 1024.0:N1} KB";

            sb.AppendLine($"[{att.Id}] {att.OriginalFileName}");
            sb.AppendLine($"  Size: {sizeDisplay}");
            sb.AppendLine($"  Type: {att.ContentType}");
            sb.AppendLine(
                $"  Uploaded: {att.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");

            if (!string.IsNullOrEmpty(att.Description))
                sb.AppendLine($"  Note: {att.Description}");

            sb.AppendLine();
        }

        return ChatToolResult.Success(sb.ToString().TrimEnd());
    }
}
