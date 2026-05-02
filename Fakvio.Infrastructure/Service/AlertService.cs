using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Alert;
using Fakvio.Domain.Entities;
using Fakvio.Domain.Enums;
using Fakvio.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Default implementation of <see cref="IAlertService"/>.
/// All queries are tenant-scoped via <see cref="TenantDbContext"/>.
/// </summary>
public class AlertService : IAlertService
{
    private readonly TenantDbContext _context;
    private readonly ILogger<AlertService> _logger;

    public AlertService(TenantDbContext context, ILogger<AlertService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AlertDto> CreateAsync(
        EAlertType type,
        long relatedEntityId,
        string relatedEntityType,
        string message,
        CancellationToken ct = default)
    {
        // Idempotency: if an open alert of the same type already exists for this entity,
        // return it instead of creating a duplicate. This prevents flooding the alert list
        // when a recurring event (e.g., repeated payment matching) fires multiple times.
        var existing = await _context.Alert
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.Type == type
                  && a.RelatedEntityId == relatedEntityId
                  && a.RelatedEntityType == relatedEntityType
                  && a.ResolvedAt == null,
                ct);

        if (existing != null)
        {
            _logger.LogDebug(
                "Alert already exists for {Type}/{EntityType}/{EntityId} — skipping create",
                type, relatedEntityType, relatedEntityId);
            return MapToDto(existing);
        }

        var alert = new Alert
        {
            Type = type,
            RelatedEntityId = relatedEntityId,
            RelatedEntityType = relatedEntityType,
            Message = message
        };

        _context.Alert.Add(alert);
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Alert {AlertId} created: {Type} for {EntityType}/{EntityId}",
            alert.Id, type, relatedEntityType, relatedEntityId);

        return MapToDto(alert);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AlertDto>> GetAsync(
        bool unresolvedOnly = true,
        CancellationToken ct = default)
    {
        var query = _context.Alert.AsNoTracking();

        if (unresolvedOnly)
            query = query.Where(a => a.ResolvedAt == null);

        var alerts = await query
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => MapToDto(a))
            .ToListAsync(ct);

        return alerts;
    }

    /// <inheritdoc />
    public async Task<AlertDashboardDto> GetDashboardAsync(CancellationToken ct = default)
    {
        // Count all open alerts in a single query.
        var totalOpen = await _context.Alert
            .AsNoTracking()
            .CountAsync(a => a.ResolvedAt == null, ct);

        // Retrieve up to 5 most recent open alerts for the tile list.
        var recent = await _context.Alert
            .AsNoTracking()
            .Where(a => a.ResolvedAt == null)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .Select(a => MapToDto(a))
            .ToListAsync(ct);

        return new AlertDashboardDto
        {
            TotalOpenCount = totalOpen,
            RecentAlerts = recent
        };
    }

    /// <inheritdoc />
    public async Task<AlertDto> ResolveAsync(
        long alertId,
        long? resolvedByUserId,
        CancellationToken ct = default)
    {
        var alert = await _context.Alert
            .FirstOrDefaultAsync(a => a.Id == alertId, ct)
            ?? throw new KeyNotFoundException($"Alert {alertId} not found.");

        // Idempotent — if already resolved, return as-is without re-saving.
        if (alert.ResolvedAt.HasValue)
        {
            _logger.LogDebug("Alert {AlertId} is already resolved — no-op", alertId);
            return MapToDto(alert);
        }

        alert.ResolvedAt = DateTime.UtcNow;
        alert.ResolvedByUserId = resolvedByUserId;

        await _context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Alert {AlertId} resolved by user {UserId}",
            alertId, resolvedByUserId);

        return MapToDto(alert);
    }

    // ─── Mapping ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps an <see cref="Alert"/> entity to <see cref="AlertDto"/>.
    /// Static helper used in both in-memory mapping and LINQ projection.
    /// </summary>
    private static AlertDto MapToDto(Alert a) => new()
    {
        Id = a.Id,
        Type = a.Type,
        RelatedEntityId = a.RelatedEntityId,
        RelatedEntityType = a.RelatedEntityType,
        Message = a.Message,
        CreatedAt = a.CreatedAt,
        ResolvedAt = a.ResolvedAt
    };
}
