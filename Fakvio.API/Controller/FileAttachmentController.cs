using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.FileAttachment;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for managing file attachments on any entity.
/// Provides upload (multipart/form-data), download, list, and delete endpoints.
///
/// Files are stored in Azure Blob Storage via IFileStorage. Metadata is stored
/// in the tenant database (FileAttachment entity). The polymorphic key
/// (EntityName + RecordId) allows attaching files to any entity type.
///
/// All endpoints require authentication — tenant isolation is automatic
/// via JWT CompanyId claim and TenantDbContext scoping.
/// </summary>
[ApiController]
[Route("api/file-attachment")]
[Produces("application/json")]
[Authorize]
public class FileAttachmentController : ControllerBase
{
    private readonly IFileAttachmentService _fileAttachmentService;
    private readonly ILogger<FileAttachmentController> _logger;

    /// <summary>
    /// Maximum file size accepted by the upload endpoint (50 MB).
    /// Must match the limit enforced in FileAttachmentService for consistency.
    /// </summary>
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;

    public FileAttachmentController(
        IFileAttachmentService fileAttachmentService,
        ILogger<FileAttachmentController> logger)
    {
        _fileAttachmentService = fileAttachmentService;
        _logger = logger;
    }

    /// <summary>
    /// Uploads a file attachment for a given entity.
    /// Accepts multipart/form-data with the file and metadata fields.
    ///
    /// Form fields:
    /// - file: The file to upload (IFormFile)
    /// - entityName: Entity type (e.g., "Invoice", "Client")
    /// - recordId: ID of the entity record
    /// - description: Optional description (max 500 chars)
    /// </summary>
    /// <returns>The created FileAttachmentDto with metadata.</returns>
    [HttpPost("upload")]
    [ProducesResponseType(typeof(FileAttachmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [RequestSizeLimit(MaxFileSizeBytes + 1024)] // Slightly more than max to account for multipart overhead
    public async Task<ActionResult<FileAttachmentDto>> Upload(
        IFormFile file,
        [FromForm] string entityName,
        [FromForm] long recordId,
        [FromForm] string? description,
        CancellationToken ct)
    {
        // Validate required fields
        if (file == null || file.Length == 0)
        {
            return BadRequest(new { message = "No file provided." });
        }

        if (string.IsNullOrWhiteSpace(entityName))
        {
            return BadRequest(new { message = "EntityName is required." });
        }

        if (recordId <= 0)
        {
            return BadRequest(new { message = "RecordId must be a positive number." });
        }

        if (file.Length > MaxFileSizeBytes)
        {
            return BadRequest(new { message = $"File size exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB." });
        }

        // Read file bytes from the multipart stream
        using var memoryStream = new MemoryStream();
        await file.CopyToAsync(memoryStream, ct);

        var uploadDto = new FileAttachmentUploadDto
        {
            EntityName = entityName,
            RecordId = recordId,
            FileName = file.FileName,
            ContentType = file.ContentType ?? "application/octet-stream",
            FileContent = memoryStream.ToArray(),
            Description = description
        };

        try
        {
            var result = await _fileAttachmentService.UploadAsync(uploadDto, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "File upload rejected for {EntityName} #{RecordId}", entityName, recordId);
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>
    /// Downloads a file attachment by its ID.
    /// Returns the file with the correct Content-Type and Content-Disposition headers
    /// so the browser triggers a file download with the original file name.
    /// </summary>
    /// <param name="id">FileAttachment ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The file content as a downloadable response.</returns>
    [HttpGet("{id:long}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(long id, CancellationToken ct)
    {
        var result = await _fileAttachmentService.DownloadAsync(id, ct);

        if (result == null)
        {
            return NotFound(new { message = $"File attachment #{id} not found." });
        }

        var (content, fileName, contentType) = result.Value;

        // Return file with Content-Disposition: attachment so browser downloads it
        return File(content, contentType, fileName);
    }

    /// <summary>
    /// Lists all file attachments for a given entity record.
    /// Results are ordered by CreatedAt descending (newest first).
    /// </summary>
    /// <param name="entityName">Entity type (e.g., "Invoice", "Client").</param>
    /// <param name="recordId">ID of the entity record.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of FileAttachmentDto.</returns>
    [HttpGet("{entityName}/{recordId:long}")]
    [ProducesResponseType(typeof(List<FileAttachmentDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<FileAttachmentDto>>> GetByEntity(
        string entityName, long recordId, CancellationToken ct)
    {
        var attachments = await _fileAttachmentService.GetByEntityAsync(entityName, recordId, ct);
        return Ok(attachments);
    }

    /// <summary>
    /// Deletes a file attachment by its ID.
    /// Removes both the blob from storage and the metadata record from the database.
    /// </summary>
    /// <param name="id">FileAttachment ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>NoContent on success, NotFound if the attachment doesn't exist.</returns>
    [HttpDelete("{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(long id, CancellationToken ct)
    {
        var deleted = await _fileAttachmentService.DeleteAsync(id, ct);

        if (!deleted)
        {
            return NotFound(new { message = $"File attachment #{id} not found." });
        }

        return NoContent();
    }
}
