// Manual Functions wrapper for ApiKeyController.
// Written by hand on purpose: the Fakvio.Functions.Generator is no longer wired in
// as an analyzer (see Fakvio.Functions.csproj), so nothing generates this file.
// Endpoints: GetAll, Create, Revoke (current user's API keys).

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.ApiKey;

namespace Fakvio.Functions.Generated;

/// <summary>
/// Azure Functions HTTP triggers wrapping <see cref="ApiKeyController"/>.
/// Route prefix: api/api-key. All endpoints require [Authorize] — the JWT is
/// validated by JwtAuthenticationMiddleware before the function runs.
/// </summary>
public class ApiKeyFunctions
{
    private readonly ApiKeyController _controller;

    public ApiKeyFunctions(ApiKeyController controller)
    {
        _controller = controller;
    }

    /// <summary>
    /// GET api/api-key → ApiKeyController.GetAll
    /// </summary>
    [Function("ApiKey_GetAll")]
    public async Task<IActionResult> ApiKey_GetAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/api-key")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetAll(ct));
    }

    /// <summary>
    /// GET api/api-key/me → ApiKeyController.Me
    /// </summary>
    [Function("ApiKey_Me")]
    public IActionResult ApiKey_Me(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/api-key/me")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        return FunctionResultHelper.Normalize(_controller.Me());
    }

    /// <summary>
    /// POST api/api-key → ApiKeyController.Create
    /// </summary>
    [Function("ApiKey_Create")]
    public async Task<IActionResult> ApiKey_Create(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/api-key")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        var dto = await System.Text.Json.JsonSerializer.DeserializeAsync<CreateApiKeyDto>(
            req.Body, FunctionResultHelper.JsonOptions, ct);

        if (dto == null)
            return new BadRequestObjectResult(new { message = "Invalid request body." });

        return FunctionResultHelper.Normalize(await _controller.Create(dto, ct));
    }

    /// <summary>
    /// POST api/api-key/{id}/revoke → ApiKeyController.Revoke
    /// </summary>
    [Function("ApiKey_Revoke")]
    public async Task<IActionResult> ApiKey_Revoke(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/api-key/{id:long}/revoke")] HttpRequest req,
        string id)
    {
        // Azure Functions cannot bind a long route parameter directly — parse it here.
        if (!long.TryParse(id, out var parsedId))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'id'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Revoke(parsedId, ct));
    }
}
