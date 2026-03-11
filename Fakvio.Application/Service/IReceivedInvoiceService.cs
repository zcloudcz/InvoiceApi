using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.ReceivedInvoice;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for managing received (incoming) invoices — expenses from suppliers.
/// Handles CRUD, status transitions, and filtering.
/// </summary>
public interface IReceivedInvoiceService
{
    /// <summary>
    /// Gets all received invoices with optional filtering.
    /// </summary>
    Task<List<ReceivedInvoiceDto>> GetAllAsync(
        EReceivedInvoiceStatus? status = null,
        long? supplierId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets paginated, filtered and sorted received invoices.
    /// </summary>
    Task<PagedResult<ReceivedInvoiceDto>> GetPagedAsync(
        ReceivedInvoiceFilterDto filter,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a specific received invoice by ID.
    /// </summary>
    Task<ReceivedInvoiceDto?> GetByIdAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Creates a new received invoice.
    /// Calculates item totals and invoice totals automatically.
    /// </summary>
    Task<ReceivedInvoiceDto> CreateAsync(CreateReceivedInvoiceDto dto, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing received invoice.
    /// Only allowed in Received or Approved status.
    /// </summary>
    Task<ReceivedInvoiceDto?> UpdateAsync(long id, UpdateReceivedInvoiceDto dto, CancellationToken ct = default);

    /// <summary>
    /// Approves a received invoice for payment.
    /// Transition: Received -> Approved.
    /// </summary>
    Task<ReceivedInvoiceDto?> ApproveAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Marks a received invoice as paid.
    /// Transition: Approved -> Paid.
    /// </summary>
    Task<ReceivedInvoiceDto?> MarkAsPaidAsync(long id, DateTime? paidAt = null, CancellationToken ct = default);

    /// <summary>
    /// Rejects a received invoice.
    /// Transition: Received -> Rejected.
    /// </summary>
    Task<ReceivedInvoiceDto?> RejectAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes a received invoice.
    /// Only allowed in Received or Rejected status.
    /// </summary>
    Task<bool> DeleteAsync(long id, CancellationToken ct = default);
}
