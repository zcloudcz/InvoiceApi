using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Represents a file attached to any entity in the system.
/// Uses an entity-agnostic design: EntityName + RecordId form a polymorphic foreign key
/// that can point to Invoice, Client, ReceivedInvoice, or any other entity without
/// requiring a hard FK constraint or enum for each entity type.
///
/// The actual file bytes are stored in external blob storage (Azure Blob Storage).
/// This entity only holds the metadata and the blob path needed to retrieve the file.
///
/// Example: An invoice (Id=42) with two attachments:
///   EntityName = "Invoice", RecordId = 42, FileGuid = "a1b2...", OriginalFileName = "contract.pdf"
///   EntityName = "Invoice", RecordId = 42, FileGuid = "c3d4...", OriginalFileName = "scan.jpg"
/// </summary>
public class FileAttachment : BaseEntity
{
    /// <summary>
    /// Name of the entity type this file is attached to (e.g., "Invoice", "Client", "ReceivedInvoice").
    /// This is a free-form string matching the entity class name — no enum needed.
    /// Adding attachments to a new entity type requires zero code changes.
    /// </summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Primary key (Id) of the record this file is attached to in the entity's table.
    /// Combined with EntityName, this uniquely identifies the owning record.
    /// Example: If EntityName = "Invoice" and RecordId = 42, the file belongs to Invoice with Id = 42.
    /// </summary>
    public long RecordId { get; set; }

    /// <summary>
    /// Globally unique identifier for the blob in external storage.
    /// Used as part of the blob name to prevent collisions and enable direct lookup.
    /// Generated automatically on creation — never changes after that.
    /// </summary>
    public Guid FileGuid { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Original file name as uploaded by the user (e.g., "contract.pdf", "scan-front.jpg").
    /// Preserved for display and download purposes — the actual blob name uses FileGuid.
    /// </summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME type of the file (e.g., "application/pdf", "image/png", "image/jpeg").
    /// Set during upload based on the file content or extension.
    /// Used to set the correct Content-Type header when downloading.
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Size of the file in bytes.
    /// Used for display (formatted as KB/MB) and for enforcing upload size limits.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Optional user-provided description or note about this attachment.
    /// Example: "Signed contract", "Original scan from client", "Supporting document".
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Full path to the blob in external storage.
    /// Format: "{EntityName}/{RecordId}/{FileGuid}{extension}" (e.g., "Invoice/42/a1b2c3d4.pdf").
    /// Stored so the system can retrieve or delete the blob without recalculating the path.
    /// </summary>
    public string BlobPath { get; set; } = string.Empty;
}
