// Manual Functions wrapper for UserPreferencesController.
// Endpoints: Get, Update (current user's UI preferences).

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.User;

namespace Fakvio.Functions.Generated;

/// <summary>
/// Azure Functions HTTP triggers wrapping <see cref="UserPreferencesController"/>.
/// Route prefix: api/user-preferences.
/// All endpoints require [Authorize] — JWT validated by middleware.
/// </summary>
public class UserPreferencesFunctions
{
    private readonly UserPreferencesController _controller;

    public UserPreferencesFunctions(UserPreferencesController controller)
    {
        _controller = controller;
    }

    /// <summary>
    /// GET api/user-preferences → UserPreferencesController.Get
    /// </summary>
    [Function("UserPreferences_Get")]
    public async Task<IActionResult> UserPreferences_Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/user-preferences")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.Get(ct));
    }

    /// <summary>
    /// PUT api/user-preferences → UserPreferencesController.Update
    /// </summary>
    [Function("UserPreferences_Update")]
    public async Task<IActionResult> UserPreferences_Update(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "api/user-preferences")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        var dto = await System.Text.Json.JsonSerializer.DeserializeAsync<UserPreferencesDto>(
            req.Body, FunctionResultHelper.JsonOptions, ct);

        if (dto == null)
            return new BadRequestObjectResult(new { message = "Invalid request body." });

        return FunctionResultHelper.Normalize(await _controller.Update(dto, ct));
    }
}
