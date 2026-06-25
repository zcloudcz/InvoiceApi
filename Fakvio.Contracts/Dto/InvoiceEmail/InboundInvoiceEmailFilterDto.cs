using Fakvio.Contracts.Common.Pagination;
using Fakvio.Domain.Enums;

namespace Fakvio.Contracts.Dto.InvoiceEmail;

/// <summary>
/// Filter parameters for the inbound invoice email inbox list.
/// </summary>
public class InboundInvoiceEmailFilterDto : PaginationParams
{
    /// <summary>Filter by processing status.</summary>
    public EInvoiceEmailStatus? Status { get; set; }

    /// <summary>Filter by classified direction (Received/Issued).</summary>
    public EInvoiceDirection? Direction { get; set; }

    /// <summary>Search in from address, subject.</summary>
    public string? Search { get; set; }
}
