// Manual Functions wrapper for ImportController.
// Route: api/import — 2 endpoints: Preview (multipart), Confirm (JSON).

#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.Import;
using Fakvio.Domain.Enums;

namespace Fakvio.Functions.Generated;

public class ImportFunctions
{
    private readonly ImportController _controller;

    public ImportFunctions(ImportController controller)
    {
        _controller = controller;
    }

    /// <summary>
    /// POST api/import/preview — multipart/form-data with PDF files + target enum.
    /// </summary>
    [Function("Import_Preview")]
    public async Task<IActionResult> Import_Preview(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/import/preview")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;

        var files = req.Form.Files.GetFiles("files").ToList();
        System.Enum.TryParse<EImportTarget>(
            req.Form.TryGetValue("target", out var targetVal) ? targetVal.ToString() : "0",
            out var target);

        return FunctionResultHelper.Normalize(
            await _controller.Preview(files.Cast<IFormFile>().ToList(), target, ct));
    }

    /// <summary>
    /// POST api/import/confirm — JSON body with confirmed import data.
    /// </summary>
    [Function("Import_Confirm")]
    public async Task<IActionResult> Import_Confirm(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/import/confirm")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;

        var dto = await System.Text.Json.JsonSerializer.DeserializeAsync<ConfirmInvoiceImportRequest>(
            req.Body, FunctionResultHelper.JsonOptions, ct);

        if (dto == null)
            return new BadRequestObjectResult(new { message = "Invalid request body." });

        return FunctionResultHelper.Normalize(await _controller.Confirm(dto, ct));
    }
}
