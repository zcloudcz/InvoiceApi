using Fakvio.Contracts.Dto.FileAttachment;

namespace Fakvio.Application.Service;

/// <summary>
/// Business logic layer for file attachments.
/// Coordinates between the FileAttachment EF Core entity (metadata in DB)
/// and IFileStorage (actual file bytes in blob storage).
///
/// This service handles:
/// - Generating FileGuid and blob paths on upload
/// - Saving metadata to the tenant database
/// - Proxying download requests through blob storage
/// - Cleaning up both DB records and blobs on delete
///
/// Tenant isolation is automatic — uses TenantDbContext (scoped per request)
/// and resolves the correct blob container via ITenantResolver.
/// </summary>
public interface IFileAttachmentService
{
    /// <summary>
    /// Uploads a file and creates a FileAttachment record in the database.
    /// Steps: generate FileGuid → build blob path → upload to IFileStorage → save entity.
    /// </summary>
    /// <param name="upload">Upload data including entity reference, file bytes, and optional description.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created FileAttachment as a DTO (without file bytes).</returns>
    Task<FileAttachmentDto> UploadAsync(FileAttachmentUploadDto upload, CancellationToken ct = default);

    /// <summary>
    /// Uploads a file with an explicit company ID — for use in background workers
    /// where ITenantResolver cannot resolve CompanyId from HTTP context.
    /// </summary>
    Task<FileAttachmentDto> UploadAsync(FileAttachmentUploadDto upload, long companyId, CancellationToken ct = default);

    /// <summary>
    /// Downloads file bytes by FileAttachment ID.
    /// Loads the metadata from DB, then retrieves actual bytes from blob storage.
    /// Returns a tuple with content, file name, and MIME type for the controller to build the response.
    /// </summary>
    /// <param name="attachmentId">The FileAttachment.Id in the tenant database.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (file bytes, original file name, content type), or null if not found.</returns>
    Task<(byte[] Content, string FileName, string ContentType)?> DownloadAsync(
        long attachmentId, CancellationToken ct = default);

    /// <summary>
    /// Lists all file attachments for a given entity record.
    /// Example: GetByEntityAsync("Invoice", 42) returns all files attached to Invoice #42.
    /// Results are ordered by CreatedAt descending (newest first).
    /// </summary>
    /// <param name="entityName">Entity type name (e.g., "Invoice", "Client").</param>
    /// <param name="recordId">Primary key of the entity record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of attachment DTOs (without file bytes).</returns>
    Task<List<FileAttachmentDto>> GetByEntityAsync(
        string entityName, long recordId, CancellationToken ct = default);

    /// <summary>
    /// Deletes a file attachment — removes both the database record and the blob in storage.
    /// Blob deletion is attempted first; if it fails, the DB record is NOT deleted
    /// (prevents orphaned DB records pointing to existing blobs).
    /// </summary>
    /// <param name="attachmentId">The FileAttachment.Id to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if both blob and DB record were deleted; false if the attachment was not found.</returns>
    Task<bool> DeleteAsync(long attachmentId, CancellationToken ct = default);
}
