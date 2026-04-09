namespace Fakvio.Contracts.Dto.FileAttachment;

/// <summary>
/// Read DTO returned from the API when listing or retrieving file attachments.
/// Contains metadata only — actual file bytes are downloaded via a separate endpoint.
/// </summary>
public class FileAttachmentDto
{
    /// <summary>
    /// Unique identifier of the file attachment record.
    /// Used for download and delete operations.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Entity type this file is attached to (e.g., "Invoice", "Client").
    /// </summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Primary key of the owning entity record.
    /// </summary>
    public long RecordId { get; set; }

    /// <summary>
    /// Globally unique blob identifier.
    /// </summary>
    public Guid FileGuid { get; set; }

    /// <summary>
    /// Original file name as uploaded by the user (e.g., "contract.pdf").
    /// </summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME type of the file (e.g., "application/pdf").
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes. UI formats this as KB/MB for display.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Optional user-provided description of the attachment.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the file was uploaded.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// ID of the user who uploaded the file.
    /// </summary>
    public long? CreatedByUserId { get; set; }
}
