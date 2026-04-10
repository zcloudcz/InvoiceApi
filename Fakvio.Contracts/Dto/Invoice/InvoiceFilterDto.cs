using Fakvio.Contracts.Common.Pagination;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.Invoice;

/// <summary>
/// Filter parameters for invoices list
/// </summary>
public class InvoiceFilterDto : PaginationParams
{
    /// <summary>
    /// Search in document number, client name, variable symbol
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// Filter by document type
    /// </summary>
    public EDocumentType? DocumentType { get; set; }

    /// <summary>
    /// Filter by status
    /// </summary>
    public EInvoiceStatus? Status { get; set; }

    /// <summary>
    /// Filter by client ID
    /// </summary>
    public long? ClientId { get; set; }

    /// <summary>
    /// Filter by issuer ID
    /// </summary>
    public long? IssuerId { get; set; }

    /// <summary>
    /// Filter by issue date from
    /// </summary>
    public DateTime? IssueDateFrom { get; set; }

    /// <summary>
    /// Filter by issue date to
    /// </summary>
    public DateTime? IssueDateTo { get; set; }

    /// <summary>
    /// Filter by due date from
    /// </summary>
    public DateTime? DueDateFrom { get; set; }

    /// <summary>
    /// Filter by due date to
    /// </summary>
    public DateTime? DueDateTo { get; set; }

    /// <summary>
    /// Filter by taxable supply date (DUZP — datum uskutečnění zdanitelného plnění) — lower bound
    /// </summary>
    public DateTime? TaxableSupplyDateFrom { get; set; }

    /// <summary>
    /// Filter by taxable supply date (DUZP) — upper bound
    /// </summary>
    public DateTime? TaxableSupplyDateTo { get; set; }

    /// <summary>
    /// Show only overdue invoices
    /// </summary>
    public bool? IsOverdue { get; set; }

    /// <summary>
    /// Filter by currency
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Minimum total amount
    /// </summary>
    public decimal? MinAmount { get; set; }

    /// <summary>
    /// Maximum total amount
    /// </summary>
    public decimal? MaxAmount { get; set; }
}
