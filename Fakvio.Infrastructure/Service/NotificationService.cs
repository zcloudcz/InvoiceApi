using Fakvio.Application.Service;
using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Notification;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Default implementation of <see cref="INotificationService"/>.
/// Business data lives in <see cref="TenantDbContext"/>; user list is read
/// cross-context from <see cref="MasterDbContext"/> (read-only).
/// </summary>
public class NotificationService : INotificationService
{
    private readonly TenantDbContext _context;
    private readonly MasterDbContext _masterContext;
    private readonly ITenantResolver _tenantResolver;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        TenantDbContext context,
        MasterDbContext masterContext,
        ITenantResolver tenantResolver,
        ILogger<NotificationService> logger)
    {
        _context = context;
        _masterContext = masterContext;
        _tenantResolver = tenantResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long> CreateForAllUsersAsync(
        ENotificationType type,
        string title,
        string message,
        long relatedEntityId,
        string relatedEntityType,
        CancellationToken ct = default)
    {
        var companyId = _tenantResolver.GetCurrentCompanyId();

        // Fallback for background workers where HTTP context is unavailable:
        // resolve companyId from tenant schema name via CompanySystemSettings.
        if (companyId == null && !string.IsNullOrEmpty(_context.Schema))
        {
            var settings = await _masterContext.Set<CompanySystemSettings>()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.SchemaName == _context.Schema, ct);
            companyId = settings?.CompanyId;
        }

        if (companyId == null || companyId == 0)
        {
            _logger.LogWarning("Cannot create notification — no tenant context (schema={Schema})", _context.Schema);
            return 0;
        }

        return await CreateForAllUsersAsync(type, title, message, relatedEntityId, relatedEntityType, companyId.Value, ct);
    }

    /// <inheritdoc />
    public async Task<long> CreateForAllUsersAsync(
        ENotificationType type,
        string title,
        string message,
        long relatedEntityId,
        string relatedEntityType,
        long companyId,
        CancellationToken ct = default)
    {

        var userIds = await _masterContext.Set<User>()
            .AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(ct);

        if (userIds.Count == 0)
        {
            _logger.LogDebug("No active users for company {CompanyId} — notification skipped", companyId);
            return 0;
        }

        var notification = new Notification
        {
            Type = type,
            Title = title,
            Message = message,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType
        };

        foreach (var userId in userIds)
        {
            notification.Recipients.Add(new NotificationRecipient { UserId = userId });
        }

        _context.Notification.Add(notification);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Notification {NotificationId} ({Type}) created for {UserCount} users — entity {EntityType}/{EntityId}",
            notification.Id, type, userIds.Count, relatedEntityType, relatedEntityId);

        return notification.Id;
    }

    /// <inheritdoc />
    public async Task<int> GetUnreadCountAsync(long userId, CancellationToken ct = default)
    {
        return await _context.NotificationRecipient
            .AsNoTracking()
            .CountAsync(r => r.UserId == userId && r.ReadAt == null, ct);
    }

    /// <inheritdoc />
    public async Task<PagedResult<NotificationDto>> GetForUserAsync(
        long userId,
        NotificationFilterDto filter,
        CancellationToken ct = default)
    {
        var query = _context.NotificationRecipient
            .AsNoTracking()
            .Include(r => r.Notification)
            .Where(r => r.UserId == userId);

        if (filter.UnreadOnly == true)
            query = query.Where(r => r.ReadAt == null);

        if (filter.Type.HasValue)
            query = query.Where(r => r.Notification.Type == filter.Type.Value);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(r => r.Notification.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(r => new NotificationDto
            {
                Id = r.Notification.Id,
                Type = r.Notification.Type,
                Title = r.Notification.Title,
                Message = r.Notification.Message,
                RelatedEntityId = r.Notification.RelatedEntityId,
                RelatedEntityType = r.Notification.RelatedEntityType,
                CreatedAt = r.Notification.CreatedAt,
                IsRead = r.ReadAt != null,
                ReadAt = r.ReadAt
            })
            .ToListAsync(ct);

        return new PagedResult<NotificationDto>(items, totalCount, filter.Page, filter.PageSize);
    }

    /// <inheritdoc />
    public async Task<NotificationDashboardDto> GetDashboardAsync(long userId, CancellationToken ct = default)
    {
        var unreadCount = await GetUnreadCountAsync(userId, ct);

        var recent = await _context.NotificationRecipient
            .AsNoTracking()
            .Include(r => r.Notification)
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.Notification.CreatedAt)
            .Take(10)
            .Select(r => new NotificationDto
            {
                Id = r.Notification.Id,
                Type = r.Notification.Type,
                Title = r.Notification.Title,
                Message = r.Notification.Message,
                RelatedEntityId = r.Notification.RelatedEntityId,
                RelatedEntityType = r.Notification.RelatedEntityType,
                CreatedAt = r.Notification.CreatedAt,
                IsRead = r.ReadAt != null,
                ReadAt = r.ReadAt
            })
            .ToListAsync(ct);

        return new NotificationDashboardDto
        {
            UnreadCount = unreadCount,
            RecentNotifications = recent
        };
    }

    /// <inheritdoc />
    public async Task MarkAsReadAsync(long notificationId, long userId, CancellationToken ct = default)
    {
        var recipient = await _context.NotificationRecipient
            .FirstOrDefaultAsync(r => r.NotificationId == notificationId && r.UserId == userId, ct);

        if (recipient == null)
        {
            _logger.LogDebug("NotificationRecipient not found for notification {NotificationId} / user {UserId}", notificationId, userId);
            return;
        }

        if (recipient.ReadAt.HasValue)
            return;

        recipient.ReadAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task MarkAllAsReadAsync(long userId, CancellationToken ct = default)
    {
        var unread = await _context.NotificationRecipient
            .Where(r => r.UserId == userId && r.ReadAt == null)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        foreach (var recipient in unread)
        {
            recipient.ReadAt = now;
        }

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Marked {Count} notifications as read for user {UserId}", unread.Count, userId);
    }
}
