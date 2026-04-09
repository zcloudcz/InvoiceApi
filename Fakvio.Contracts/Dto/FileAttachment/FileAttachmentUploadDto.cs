using System.ComponentModel.DataAnnotations;

namespace Fakvio.Contracts.Dto.FileAttachment;

/// <summary>
/// Write DTO sent from the UI when uploading a file attachment.
/// Contains the entity reference, file metadata, and actual file bytes.
///
/// Note: In the API controller, this is built from IFormFile + form fields.
/// In the Blazor UI, this is built from IBrowserFile data.
/// </summary>
public class FileAttachmentUploadDto
{
    /// <summary>
    /// Entity type to attach the file to (e.g., "Invoice", "Client").
    /// Must match the entity class name exactly.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Primary key of the entity record to attach the file to.
    /// </summary>
    [Required]
    public long RecordId { get; set; }

    /// <summary>
    /// Original file name (e.g., "contract.pdf").
    /// </summary>
    [Required]
    [StringLength(500)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME type of the file (e.g., "application/pdf", "image/png").
    /// </summary>
    [Required]
    [StringLength(200)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Actual file content as a byte array.
    /// Size is validated against the configured maximum (default 50 MB).
    /// </summary>
    [Required]
    public byte[] FileContent { get; set; } = [];

    /// <summary>
    /// Optional description or note about the file.
    /// </summary>
    [StringLength(500)]
    public string? Description { get; set; }
}
