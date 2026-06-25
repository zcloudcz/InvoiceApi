using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Notification;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fakvio.API.Controller;

/// <summary>
/// Controller for per-user in-app notifications.
///
/// All endpoints require authentication. The current user's ID (from JWT)
/// scopes all queries — users can only see their own notifications.
/// Tenant isolation is automatic via JWT CompanyId → TenantDbContext.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
[Authorize]
public class NotificationController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(
        INotificationService notificationService,
        ICurrentUserService currentUserService,
        ILogger<NotificationController> logger)
    {
        _notificationService = notificationService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    /// <summary>
    /// Returns paginated notifications for the current user.
    /// Supports optional filters: unreadOnly, type.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<NotificationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<NotificationDto>>> GetNotifications(
        [FromQuery] NotificationFilterDto filter,
        CancellationToken ct = default)
    {
        var userId = _currentUserService.GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var result = await _notificationService.GetForUserAsync(userId.Value, filter, ct);
        return Ok(result);
    }

    /// <summary>
    /// Returns the unread notification count for the current user.
    /// Used by the bell icon badge in the UI.
    /// </summary>
    [HttpGet("unread-count")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    public async Task<ActionResult<int>> GetUnreadCount(CancellationToken ct = default)
    {
        var userId = _currentUserService.GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var count = await _notificationService.GetUnreadCountAsync(userId.Value, ct);
        return Ok(count);
    }

    /// <summary>
    /// Returns a dashboard summary: unread count + up to 10 recent notifications.
    /// Used by the notification dropdown in the AppBar.
    /// </summary>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(NotificationDashboardDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<NotificationDashboardDto>> GetDashboard(CancellationToken ct = default)
    {
        var userId = _currentUserService.GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        var summary = await _notificationService.GetDashboardAsync(userId.Value, ct);
        return Ok(summary);
    }

    /// <summary>
    /// Marks a single notification as read for the current user.
    /// Idempotent — marking an already-read notification returns 204.
    /// </summary>
    [HttpPost("{id:long}/read")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAsRead(long id, CancellationToken ct = default)
    {
        var userId = _currentUserService.GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        await _notificationService.MarkAsReadAsync(id, userId.Value, ct);
        return NoContent();
    }

    /// <summary>
    /// Marks all unread notifications as read for the current user.
    /// </summary>
    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllAsRead(CancellationToken ct = default)
    {
        var userId = _currentUserService.GetCurrentUserId();
        if (userId == null)
            return Unauthorized();

        await _notificationService.MarkAllAsReadAsync(userId.Value, ct);
        return NoContent();
    }
}
