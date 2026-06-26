using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.FileAttachment;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Business logic layer for file attachments.
/// Coordinates between EF Core (FileAttachment metadata in tenant DB)
/// and IFileStorage (actual file bytes in Azure Blob Storage).
///
/// Tenant isolation:
/// - DB: TenantDbContext is scoped to the current tenant's schema
/// - Blob: One shared container holds all tenants; each tenant gets a top-level
///   directory named by CompanyId. Blob paths look like "42/a1b2c3d4.pdf".
///   The container name is resolved internally by IFileStorage from system settings.
///
/// This service does NOT validate whether the referenced entity (EntityName + RecordId)
/// actually exists — the caller is responsible for passing valid references.
/// This keeps the service entity-agnostic and avoids coupling to specific entity types.
/// </summary>
public class FileAttachmentService : IFileAttachmentService
{
    private readonly TenantDbContext _context;
    private readonly IFileStorage _fileStorage;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<FileAttachmentService> _logger;

    /// <summary>
    /// Maximum allowed file size in bytes (50 MB).
    /// Enforced here as a safety net — the controller should also validate before buffering.
    /// </summary>
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    /// <summary>
    /// Allowed file extensions (whitelist). Executables and scripts are rejected.
    /// </summary>
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".txt", ".rtf", ".odt", ".ods",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".svg", ".webp", ".tiff",
        ".zip", ".rar", ".7z",
        ".xml", ".json", ".html",
        ".isdoc", ".isdocx"
    };

    public FileAttachmentService(
        TenantDbContext context,
        IFileStorage fileStorage,
        ITenantResolver tenantResolver,
        ILogger<FileAttachmentService> logger)
    {
        _context = context;
        _fileStorage = fileStorage;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FileAttachmentDto> UploadAsync(
        FileAttachmentUploadDto upload, CancellationToken ct = default)
    {
        // Validate file size
        if (upload.FileContent.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException(
                $"File size ({upload.FileContent.Length} bytes) exceeds the maximum allowed size ({MaxFileSizeBytes} bytes).");
        }

        // Validate file extension (security: reject executables)
        var extension = Path.GetExtension(upload.FileName);
        if (!string.IsNullOrEmpty(extension) && !AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException(
                $"File extension '{extension}' is not allowed. Allowed extensions: {string.Join(", ", AllowedExtensions)}");
        }

        // Generate unique identifiers for this attachment.
        // Blob path is "{companyId}/{guid}{ext}" — tenant isolation lives in the path prefix.
        var fileGuid = Guid.NewGuid();
        var companyId = RequireCompanyId();
        var blobPath = BuildBlobPath(companyId, fileGuid, extension);

        // Step 1: Upload bytes to blob storage (container resolved internally by IFileStorage)
        _logger.LogWarning(
            "FileAttachment uploading: path={BlobPath}, size={Size}, contentType={ContentType}, companyId={CompanyId}, storage={StorageType}",
            blobPath, upload.FileContent.Length, upload.ContentType, companyId, _fileStorage.GetType().Name);

        await _fileStorage.UploadAsync(blobPath, upload.FileContent, upload.ContentType, ct);

        _logger.LogWarning("FileAttachment blob upload completed: path={BlobPath}", blobPath);

        // Step 2: Save metadata to tenant database
        var entity = new FileAttachment
        {
            EntityName = upload.EntityName,
            RecordId = upload.RecordId,
            FileGuid = fileGuid,
            OriginalFileName = upload.FileName,
            ContentType = upload.ContentType,
            FileSizeBytes = upload.FileContent.Length,
            Description = upload.Description,
            BlobPath = blobPath
        };

        _context.FileAttachment.Add(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Uploaded file attachment: {FileName} ({Size} bytes) for {EntityName} #{RecordId}",
            upload.FileName, upload.FileContent.Length, upload.EntityName, upload.RecordId);

        return MapToDto(entity);
    }

    /// <inheritdoc />
    public async Task<(byte[] Content, string FileName, string ContentType)?> DownloadAsync(
        long attachmentId, CancellationToken ct = default)
    {
        var entity = await _context.FileAttachment
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == attachmentId, ct);

        if (entity == null)
        {
            _logger.LogWarning("FileAttachment #{Id} not found for download", attachmentId);
            return null;
        }

        var content = await _fileStorage.DownloadAsync(entity.BlobPath, ct);

        return (content, entity.OriginalFileName, entity.ContentType);
    }

    /// <inheritdoc />
    public async Task<List<FileAttachmentDto>> GetByEntityAsync(
        string entityName, long recordId, CancellationToken ct = default)
    {
        var entities = await _context.FileAttachment
            .AsNoTracking()
            .Where(f => f.EntityName == entityName && f.RecordId == recordId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);

        return entities.Select(MapToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(long attachmentId, CancellationToken ct = default)
    {
        var entity = await _context.FileAttachment
            .FirstOrDefaultAsync(f => f.Id == attachmentId, ct);

        if (entity == null)
        {
            _logger.LogWarning("FileAttachment #{Id} not found for deletion", attachmentId);
            return false;
        }

        // Step 1: Delete blob from storage first.
        // If this fails, we don't remove the DB record (prevents orphaned references).
        await _fileStorage.DeleteAsync(entity.BlobPath, ct);

        // Step 2: Remove DB record
        _context.FileAttachment.Remove(entity);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Deleted file attachment: {FileName} for {EntityName} #{RecordId}",
            entity.OriginalFileName, entity.EntityName, entity.RecordId);

        return true;
    }

    // ─── Private Helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the blob path inside the shared container.
    /// Format: "{companyId}/{fileGuid}{extension}".
    /// Example: "42/a1b2c3d4-e5f6-7890-abcd-ef1234567890.pdf"
    ///
    /// Why CompanyId in the path? Tenant isolation. Even though all tenants share
    /// one container, each blob path begins with the owning tenant's CompanyId,
    /// so a bug elsewhere can never accidentally surface another tenant's blobs
    /// (the path prefix is part of the blob's identity).
    ///
    /// EntityName + RecordId are NOT in the path because the FileAttachment row
    /// already holds them — they're queried via the database, not the blob path.
    /// </summary>
    private static string BuildBlobPath(long companyId, Guid fileGuid, string? extension)
    {
        return $"{companyId}/{fileGuid}{extension}";
    }

    /// <summary>
    /// Returns the current tenant's CompanyId or throws if no JWT context is available.
    /// File operations are always tenant-scoped — there is no "global" attachment.
    /// </summary>
    private long RequireCompanyId()
    {
        var companyId = _tenantResolver.GetCurrentCompanyId();
        if (companyId.HasValue)
            return companyId.Value;

        // Diagnostic: log whether HttpContext exists and what claims are present
        _logger.LogError(
            "RequireCompanyId failed — IsSysAdmin={IsSysAdmin}. " +
            "This usually means ImpersonationMiddleware did not set the CompanyId claim " +
            "(SysAdmin without X-Company-Id header) or HttpContextAccessor returned wrong context.",
            _tenantResolver.IsSysAdmin());

        throw new InvalidOperationException(
            "No company context — cannot determine blob storage path. " +
            "Ensure the request has a valid JWT with CompanyId claim.");
    }

    /// <summary>
    /// Maps a FileAttachment entity to its DTO.
    /// Manual mapping (no ZMapper needed for this simple flat mapping).
    /// </summary>
    private static FileAttachmentDto MapToDto(FileAttachment entity)
    {
        return new FileAttachmentDto
        {
            Id = entity.Id,
            EntityName = entity.EntityName,
            RecordId = entity.RecordId,
            FileGuid = entity.FileGuid,
            OriginalFileName = entity.OriginalFileName,
            ContentType = entity.ContentType,
            FileSizeBytes = entity.FileSizeBytes,
            Description = entity.Description,
            CreatedAt = entity.CreatedAt,
            CreatedByUserId = entity.CreatedByUserId
        };
    }
}
