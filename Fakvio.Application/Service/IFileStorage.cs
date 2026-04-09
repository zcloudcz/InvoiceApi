namespace Fakvio.Application.Service;

/// <summary>
/// Pure storage abstraction for file upload/download/delete operations.
/// Knows nothing about the FileAttachment entity or EF Core — deals only in
/// byte arrays, container names, and blob paths.
///
/// This interface enables swapping the underlying storage provider without
/// changing business logic. Implementations:
/// - AzureBlobFileStorage (primary, production)
/// - Future: LocalFileStorage (dev/testing), S3FileStorage (AWS), MinioFileStorage (self-hosted)
///
/// Container naming convention: "tenant-{companyId}" (e.g., "tenant-42").
/// Blob path convention: "{EntityName}/{RecordId}/{FileGuid}{extension}" (e.g., "Invoice/42/a1b2c3d4.pdf").
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Uploads file bytes to external storage.
    /// Creates the container if it doesn't exist yet (lazy initialization).
    /// Sets Content-Type and Content-Disposition headers on the blob for proper download behavior.
    /// </summary>
    /// <param name="containerName">Storage container name (e.g., "tenant-42").</param>
    /// <param name="blobPath">Path within the container (e.g., "Invoice/42/a1b2c3d4.pdf").</param>
    /// <param name="content">File content as byte array.</param>
    /// <param name="contentType">MIME type (e.g., "application/pdf").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The blob path that was used (same as input blobPath).</returns>
    Task<string> UploadAsync(
        string containerName,
        string blobPath,
        byte[] content,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads file bytes from external storage.
    /// Throws if the blob does not exist.
    /// </summary>
    /// <param name="containerName">Storage container name (e.g., "tenant-42").</param>
    /// <param name="blobPath">Path within the container (e.g., "Invoice/42/a1b2c3d4.pdf").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File content as byte array.</returns>
    Task<byte[]> DownloadAsync(
        string containerName,
        string blobPath,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a file from external storage.
    /// Returns true if the blob was deleted, false if it didn't exist.
    /// Does not throw on missing blobs — idempotent operation.
    /// </summary>
    /// <param name="containerName">Storage container name (e.g., "tenant-42").</param>
    /// <param name="blobPath">Path within the container (e.g., "Invoice/42/a1b2c3d4.pdf").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the blob was actually deleted; false if it didn't exist.</returns>
    Task<bool> DeleteAsync(
        string containerName,
        string blobPath,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether a blob exists in external storage.
    /// Useful for health checks and connection testing.
    /// </summary>
    /// <param name="containerName">Storage container name (e.g., "tenant-42").</param>
    /// <param name="blobPath">Path within the container.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the blob exists; false otherwise.</returns>
    Task<bool> ExistsAsync(
        string containerName,
        string blobPath,
        CancellationToken ct = default);
}
