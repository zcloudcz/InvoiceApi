using Fakvio.Contracts.Common.Pagination;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.ReceivedInvoice;

/// <summary>
/// Filter parameters for received invoices list.
/// Inherits pagination, sorting, and adds expense-specific filters.
/// </summary>
public class ReceivedInvoiceFilterDto : PaginationParams
{
    /// <summary>
    /// Search in document number, supplier name, variable symbol.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Filter by status.
    /// </summary>
    public EReceivedInvoiceStatus? Status { get; set; }

    /// <summary>
    /// Filter by supplier (Client ID).
    /// </summary>
    public long? SupplierId { get; set; }

    /// <summary>
    /// Filter by issue date range.
    /// </summary>
    public DateTime? IssueDateFrom { get; set; }
    public DateTime? IssueDateTo { get; set; }

    /// <summary>
    /// Filter by due date range.
    /// </summary>
    public DateTime? DueDateFrom { get; set; }
    public DateTime? DueDateTo { get; set; }

    /// <summary>
    /// Show only overdue (unpaid + past due date).
    /// </summary>
    public bool? IsOverdue { get; set; }

    /// <summary>
    /// Filter by currency code.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Filter by amount range.
    /// </summary>
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
}
