namespace Fakvio.Application.Service;

/// <summary>
/// Pure storage abstraction for file upload/download/delete operations.
/// Knows nothing about the FileAttachment entity or EF Core — deals only in
/// byte arrays and blob paths.
///
/// Architecture: ONE shared blob container holds all tenant attachments,
/// with each tenant getting a top-level directory inside (named by CompanyId).
/// The container name is resolved internally by the implementation from
/// system/company settings — callers never specify it. This keeps the
/// abstraction minimal: blob path → bytes, nothing else.
///
/// Tenant isolation is the responsibility of the caller — every blob path
/// MUST start with the current tenant's CompanyId so each tenant only sees
/// its own subtree (e.g., "42/abc-123.pdf" for company 42).
///
/// Implementations:
/// - <c>AzureBlobFileStorage</c> — production (Azure Blob Storage)
/// - Future: LocalFileStorage (dev), S3FileStorage (AWS), MinioFileStorage (self-hosted)
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Uploads file bytes to the configured blob container.
    /// Creates the container if it doesn't exist yet (lazy initialization).
    /// Sets Content-Type on the blob for proper download behavior.
    /// </summary>
    /// <param name="blobPath">
    /// Full blob path inside the shared container, e.g. "42/a1b2c3d4.pdf".
    /// Must start with the tenant's CompanyId for isolation.
    /// </param>
    /// <param name="content">File content as byte array.</param>
    /// <param name="contentType">MIME type (e.g., "application/pdf").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The blob path that was used (same as input).</returns>
    Task<string> UploadAsync(
        string blobPath,
        byte[] content,
        string contentType,
        CancellationToken ct = default);

    /// <summary>
    /// Downloads file bytes from the configured blob container.
    /// Throws if the blob does not exist.
    /// </summary>
    /// <param name="blobPath">Full blob path inside the shared container.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>File content as byte array.</returns>
    Task<byte[]> DownloadAsync(
        string blobPath,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes a file from the configured blob container.
    /// Returns true if the blob was deleted, false if it didn't exist.
    /// Idempotent — does not throw on missing blobs.
    /// </summary>
    /// <param name="blobPath">Full blob path inside the shared container.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the blob was actually deleted; false if it didn't exist.</returns>
    Task<bool> DeleteAsync(
        string blobPath,
        CancellationToken ct = default);

    /// <summary>
    /// Checks whether a blob exists in the configured container.
    /// Useful for health checks and connection testing.
    /// </summary>
    /// <param name="blobPath">Full blob path inside the shared container.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the blob exists; false otherwise.</returns>
    Task<bool> ExistsAsync(
        string blobPath,
        CancellationToken ct = default);
}
