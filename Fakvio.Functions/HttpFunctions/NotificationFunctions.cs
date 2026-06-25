// Manual Functions wrapper for NotificationController.
// Route: api/notification — 5 endpoints: GetNotifications, GetUnreadCount, GetDashboard, MarkAsRead, MarkAllAsRead.

#nullable enable

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Fakvio.API.Controller;
using Fakvio.Contracts.Dto.Notification;

namespace Fakvio.Functions.Generated;

public class NotificationFunctions
{
    private readonly NotificationController _controller;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public NotificationFunctions(
        NotificationController controller,
        IHttpContextAccessor httpContextAccessor)
    {
        _controller = controller;
        _httpContextAccessor = httpContextAccessor;
    }

    [Function("Notification_GetNotifications")]
    public async Task<IActionResult> Notification_GetNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/notification")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };
        _httpContextAccessor.HttpContext = req.HttpContext;

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var filter = new NotificationFilterDto();
        if (int.TryParse(req.Query["page"], out var page)) filter.Page = page;
        if (int.TryParse(req.Query["pageSize"], out var pageSize)) filter.PageSize = pageSize;
        if (bool.TryParse(req.Query["unreadOnly"], out var unreadOnly)) filter.UnreadOnly = unreadOnly;
        if (int.TryParse(req.Query["type"], out var type)) filter.Type = (Fakvio.Domain.Enums.ENotificationType)type;

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetNotifications(filter, ct));
    }

    [Function("Notification_GetUnreadCount")]
    public async Task<IActionResult> Notification_GetUnreadCount(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/notification/unread-count")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };
        _httpContextAccessor.HttpContext = req.HttpContext;

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetUnreadCount(ct));
    }

    [Function("Notification_GetDashboard")]
    public async Task<IActionResult> Notification_GetDashboard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "api/notification/dashboard")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };
        _httpContextAccessor.HttpContext = req.HttpContext;

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.GetDashboard(ct));
    }

    [Function("Notification_MarkAsRead")]
    public async Task<IActionResult> Notification_MarkAsRead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/notification/{id:long}/read")] HttpRequest req,
        string id)
    {
        if (!long.TryParse(id, out var idParsed))
            return new BadRequestObjectResult(new { message = "Invalid parameter 'id'." });

        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };
        _httpContextAccessor.HttpContext = req.HttpContext;

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.MarkAsRead(idParsed, ct));
    }

    [Function("Notification_MarkAllAsRead")]
    public async Task<IActionResult> Notification_MarkAllAsRead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "api/notification/read-all")] HttpRequest req)
    {
        _controller.ControllerContext = new ControllerContext { HttpContext = req.HttpContext };
        _httpContextAccessor.HttpContext = req.HttpContext;

        if (req.HttpContext.User.Identity?.IsAuthenticated != true)
            return new UnauthorizedResult();

        var ct = req.HttpContext.RequestAborted;
        return FunctionResultHelper.Normalize(await _controller.MarkAllAsRead(ct));
    }
}
