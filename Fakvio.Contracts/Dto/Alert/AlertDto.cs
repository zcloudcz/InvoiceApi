using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Alert;

/// <summary>
/// Read-model for a single alert record returned by GET /api/alert.
/// </summary>
public class AlertDto
{
    /// <summary>Alert primary key.</summary>
    public long Id { get; set; }

    /// <summary>Type of alert (e.g., OverpaidProforma).</summary>
    public EAlertType Type { get; set; }

    /// <summary>PK of the entity that triggered the alert (e.g., Invoice.Id).</summary>
    public long RelatedEntityId { get; set; }

    /// <summary>Entity type name (e.g., "Invoice").</summary>
    public string RelatedEntityType { get; set; } = string.Empty;

    /// <summary>Human-readable description of the alert condition.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>When the alert was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the alert was resolved — null means still open.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Whether this alert has been resolved.</summary>
    public bool IsResolved => ResolvedAt.HasValue;
}
