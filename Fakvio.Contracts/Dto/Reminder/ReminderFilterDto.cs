using Fakvio.Contracts.Common.Pagination;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Reminder;

/// <summary>
/// Filter parameters for paginated reminder listing.
/// Inherits page/sort from PaginationParams.
/// All filters are optional — null means "no filter".
/// </summary>
public class ReminderFilterDto : PaginationParams
{
    /// <summary>
    /// Search in invoice number, client name, or notes.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Filter by reminder status (Draft, Sent, Failed, Cancelled).
    /// </summary>
    public EReminderStatus? Status { get; set; }

    /// <summary>
    /// Filter by client ID.
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Filter by invoice ID (show all reminders for a specific invoice).
    /// </summary>
    public long? InvoiceId { get; set; }

    /// <summary>
    /// Filter by escalation level.
    /// </summary>
    public int? Level { get; set; }

    /// <summary>
    /// Reminder date range — lower bound.
    /// </summary>
    public DateTime? DateFrom { get; set; }

    /// <summary>
    /// Reminder date range — upper bound.
    /// </summary>
    public DateTime? DateTo { get; set; }
}
