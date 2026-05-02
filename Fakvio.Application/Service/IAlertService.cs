using Fakvio.Contracts.Dto.Alert;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Manages business alerts within a tenant.
///
/// Alerts are lightweight signals that something needs the user's attention
/// (e.g., an overpaid proforma). Each alert is tied to a specific entity
/// and can be resolved by the user with a single action.
///
/// The service is intentionally simple: create → list → resolve.
/// Future alert types are added by calling <see cref="CreateAsync"/> from
/// the service that detects the condition — no changes to this contract needed.
/// </summary>
public interface IAlertService
{
    /// <summary>
    /// Creates a new alert for the given entity.
    /// Idempotent per (Type, RelatedEntityId): if an OPEN alert of the same
    /// type already exists for this entity, returns the existing one instead
    /// of creating a duplicate.
    /// </summary>
    /// <param name="type">Type of alert (e.g., OverpaidProforma).</param>
    /// <param name="relatedEntityId">PK of the triggering entity.</param>
    /// <param name="relatedEntityType">Entity type name (e.g., "Invoice").</param>
    /// <param name="message">Human-readable description of the condition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created or existing alert DTO.</returns>
    Task<AlertDto> CreateAsync(
        EAlertType type,
        long relatedEntityId,
        string relatedEntityType,
        string message,
        CancellationToken ct = default);

    /// <summary>
    /// Returns open alerts for the current tenant.
    /// When <paramref name="unresolvedOnly"/> is true (default), only unresolved
    /// alerts are returned (ResolvedAt == null). Pass false to get all alerts
    /// including already-resolved ones (useful for audit/history).
    /// </summary>
    Task<IReadOnlyList<AlertDto>> GetAsync(
        bool unresolvedOnly = true,
        CancellationToken ct = default);

    /// <summary>
    /// Returns a dashboard summary: total open count + up to 5 recent alerts.
    /// </summary>
    Task<AlertDashboardDto> GetDashboardAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks an alert as resolved. Sets ResolvedAt = UtcNow and records the
    /// resolving user ID. Idempotent — resolving an already-resolved alert
    /// is a no-op that returns the existing DTO.
    /// </summary>
    /// <param name="alertId">Alert primary key.</param>
    /// <param name="resolvedByUserId">ID of the user resolving the alert.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated alert DTO.</returns>
    /// <exception cref="KeyNotFoundException">Alert not found.</exception>
    Task<AlertDto> ResolveAsync(
        long alertId,
        long? resolvedByUserId,
        CancellationToken ct = default);
}
