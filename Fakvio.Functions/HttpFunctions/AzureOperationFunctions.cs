// Manual Functions wrapper for TenantOperationController.
// Route: api/tenant-operation — 5 endpoints (SysAdmin only).

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.AzureOperation;

namespace Fakvio.Functions.Generated;

public class AzureOperationFunctions
{
    private readonly TenantOperationController _controller;

    public AzureOperationFunctions(TenantOperationController controller)
    {
        _controller = controller;
    }

    [Function("TenantOperation_Provision")]
    public async Task<IActionResult> TenantOperation_Provision(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/tenant-operation/provision")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var ct = req.HttpContext.RequestAborted;

        var dto = await System.Text.Json.JsonSerializer.DeserializeAsync<CreateTenantSchemaRequest>(
            req.Body, FunctionResultHelper.JsonOptions, ct);

        if (dto == null)
            return new BadRequestObjectResult(new { message = "Invalid request body." });

        return FunctionResultHelper.Normalize(await _controller.ProvisionTenant(dto, ct));
    }

    [Function("TenantOperation_List")]
    public async Task<IActionResult> TenantOperation_List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/tenant-operation/list")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.ListTenantSchemas(ct));
    }

    [Function("TenantOperation_Status")]
    public async Task<IActionResult> TenantOperation_Status(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/tenant-operation/status/{companyId:long}")] HttpRequest req,
        string companyId)
    {
        if (!long.TryParse(companyId, out var companyIdParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'companyId'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetTenantSchemaStatus(companyIdParsed, ct));
    }

    [Function("TenantOperation_Delete")]
    public async Task<IActionResult> TenantOperation_Delete(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "api/tenant-operation/{companyId:long}")] HttpRequest req,
        string companyId)
    {
        if (!long.TryParse(companyId, out var companyIdParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'companyId'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.DeleteTenantSchema(companyIdParsed, ct));
    }

    [Function("TenantOperation_FixSchemaPermissions")]
    public async Task<IActionResult> TenantOperation_FixSchemaPermissions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/tenant-operation/fix-permissions")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();
        if (!req.HttpContext.User.IsInRole("SysAdmin"))
            return new ForbidResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.FixSchemaPermissions(ct));
    }
}
