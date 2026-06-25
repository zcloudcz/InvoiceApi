// Manual Functions wrapper for FileAttachmentController.
// All 4 endpoints: Upload, Download, GetByEntity, Delete.

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Fakvio.API.Controller;

namespace Fakvio.Functions.Generated;

/// <summary>
/// Azure Functions HTTP triggers wrapping <see cref="FileAttachmentController"/>.
/// Route prefix: api/file-attachment.
/// All endpoints require [Authorize] — JWT validated by middleware.
/// </summary>
public class FileAttachmentFunctions
{
    private readonly FileAttachmentController _controller;

    public FileAttachmentFunctions(FileAttachmentController controller)
    {
        _controller = controller;
    }

    /// <summary>
    /// POST api/file-attachment/upload
    /// Multipart/form-data: file + entityName + recordId + description
    /// </summary>
    [Function("FileAttachment_Upload")]
    public async Task<IActionResult> FileAttachment_Upload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/file-attachment/upload")] HttpRequest req)
    {
        try
        {
            _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

            if (req.HttpContext.User.Identity?.IsAuthenticated != true)
                return new UnauthorizedResult();

            var ct = req.HttpContext.RequestAborted;

            // Multipart form data — extract file and form fields
            IFormFile? file = null;
            string entityName = "";
            long recordId = 0;
            string? description = null;

            try
            {
                file = req.Form.Files.GetFile("file");
                entityName = req.Form.TryGetValue("entityName", out var en) ? en.ToString() : "";
                long.TryParse(req.Form.TryGetValue("recordId", out var ri) ? ri.ToString() : "", out recordId);
                description = req.Form.TryGetValue("description", out var desc) ? desc.ToString() : null;
            }
            catch (Exception formEx)
            {
                return new BadRequestObjectResult(new
                {
                    message = $"Failed to parse multipart form data: {formEx.Message}",
                    error = formEx.GetType().Name
                });
            }

            if (file == null)
                return new BadRequestObjectResult(new { message = "No file found in form data (field name must be 'file')." });

            var uploadResult = await _controller.Upload(file, entityName, recordId, description, ct);
            // ActionResult<T>.Result contains the IActionResult (Ok/BadRequest/etc.)
            // ActionResult<T>.Value contains the implicit T return
            if (uploadResult.Result != null)
                return FunctionResultHelper.Normalize(uploadResult.Result);
            return new OkObjectResult(uploadResult.Value);
        }
        catch (Exception ex)
        {
            // Log to AppLog so the error is visible in SysAdmin logs page.
            // Without this, the catch swallows the exception before GlobalExceptionMiddleware sees it.
            var logger = req.HttpContext.RequestServices.GetService<Microsoft.Extensions.Logging.ILogger<FileAttachmentFunctions>>();
            logger?.LogError(ex, "FileAttachment_Upload failed: {ErrorType}: {ErrorMessage}", ex.GetType().Name, ex.Message);

            return new ObjectResult(new
            {
                message = "File upload failed.",
                error = ex.GetType().Name,
                detail = ex.Message,
                innerError = ex.InnerException?.Message
            })
            { StatusCode = 500 };
        }
    }

    /// <summary>
    /// GET api/file-attachment/{id}/download
    /// Returns the file content as a downloadable response.
    /// </summary>
    [Function("FileAttachment_Download")]
    public async Task<IActionResult> FileAttachment_Download(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/file-attachment/{id:long}/download")] HttpRequest req,
        string id)
    {
        if (!long.TryParse(id, out var idParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'id'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Download(idParsed, ct));
    }

    /// <summary>
    /// GET api/file-attachment/{entityName}/{recordId}
    /// Lists all attachments for a given entity record.
    /// </summary>
    [Function("FileAttachment_GetByEntity")]
    public async Task<IActionResult> FileAttachment_GetByEntity(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/file-attachment/{entityName}/{recordId:long}")] HttpRequest req,
        string entityName, string recordId)
    {
        if (!long.TryParse(recordId, out var recordIdParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'recordId'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetByEntity(entityName, recordIdParsed, ct));
    }

    /// <summary>
    /// DELETE api/file-attachment/{id}
    /// Deletes a file attachment by ID.
    /// </summary>
    [Function("FileAttachment_Delete")]
    public async Task<IActionResult> FileAttachment_Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "api/file-attachment/{id:long}")] HttpRequest req,
        string id)
    {
        if (!long.TryParse(id, out var idParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'id'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Delete(idParsed, ct));
    }
}
