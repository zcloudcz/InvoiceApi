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
/// - Blob: Container name is "tenant-{companyId}" (or custom prefix from settings)
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
    /// Default container prefix when not overridden in settings.
    /// Combined with companyId to form "tenant-{companyId}".
    /// </summary>
    private const string DefaultContainerPrefix = "tenant";

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
        ".xml", ".json", ".html"
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

        // Generate unique identifiers for this attachment
        var fileGuid = Guid.NewGuid();
        var blobPath = BuildBlobPath(upload.EntityName, upload.RecordId, fileGuid, extension);
        var containerName = GetContainerName();

        // Step 1: Upload bytes to blob storage
        await _fileStorage.UploadAsync(containerName, blobPath, upload.FileContent, upload.ContentType, ct);

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

        var containerName = GetContainerName();
        var content = await _fileStorage.DownloadAsync(containerName, entity.BlobPath, ct);

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
        var containerName = GetContainerName();
        await _fileStorage.DeleteAsync(containerName, entity.BlobPath, ct);

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
    /// Builds the blob path within the container.
    /// Format: "{EntityName}/{RecordId}/{FileGuid}{extension}"
    /// Example: "Invoice/42/a1b2c3d4-e5f6-7890-abcd-ef1234567890.pdf"
    /// </summary>
    private static string BuildBlobPath(string entityName, long recordId, Guid fileGuid, string? extension)
    {
        return $"{entityName}/{recordId}/{fileGuid}{extension}";
    }

    /// <summary>
    /// Resolves the blob container name for the current tenant.
    /// Format: "{prefix}-{companyId}" (e.g., "tenant-42").
    /// Azure Blob container names must be lowercase and 3-63 characters.
    /// </summary>
    private string GetContainerName()
    {
        var companyId = _tenantResolver.GetCurrentCompanyId()
            ?? throw new InvalidOperationException(
                "No company context — cannot determine blob storage container. " +
                "Ensure the request has a valid JWT with CompanyId claim.");

        // Container name: lowercase, no underscores (Azure requirement)
        return $"{DefaultContainerPrefix}-{companyId}".ToLowerInvariant();
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
