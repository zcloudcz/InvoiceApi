// Manual Functions wrapper for AlertController.
// Route: api/alert — 3 endpoints: GetAlerts, GetDashboard, ResolveAlert.

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;

namespace Fakvio.Functions.Generated;

public class AlertFunctions
{
    private readonly AlertController _controller;

    public AlertFunctions(AlertController controller)
    {
        _controller = controller;
    }

    [Function("Alert_GetAlerts")]
    public async Task<IActionResult> Alert_GetAlerts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/alert")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        bool.TryParse(req.Query["unresolvedOnly"], out var unresolvedOnly);
        if (!req.Query.ContainsKey("unresolvedOnly")) unresolvedOnly = true;

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetAlerts(unresolvedOnly, ct));
    }

    [Function("Alert_GetDashboard")]
    public async Task<IActionResult> Alert_GetDashboard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/alert/dashboard")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetDashboard(ct));
    }

    [Function("Alert_ResolveAlert")]
    public async Task<IActionResult> Alert_ResolveAlert(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/alert/{id:long}/resolve")] HttpRequest req,
        string id)
    {
        if (!long.TryParse(id, out var idParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'id'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.ResolveAlert(idParsed, ct));
    }
}
