using Fakvio.Contracts.Common.Pagination;
using Fakvio.Contracts.Dto.Invoice;
using Fakvio.Domain.Enums;

namespace Fakvio.Application.Service;

/// <summary>
/// Service for managing invoices and credit notes
/// Handles business logic for invoice operations
/// </summary>
public interface IInvoiceService
{
    /// <summary>
    /// Gets all invoices/credit notes with optional filtering
    /// </summary>
    /// <param name="documentType">Filter by document type (null = all)</param>
    /// <param name="status">Filter by status (null = all)</param>
    /// <param name="clientId">Filter by client ID (null = all)</param>
    /// <param name="issuerId">Filter by issuer ID (null = all)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of invoices</returns>
    Task<List<InvoiceDto>> GetAllInvoicesAsync(
        EDocumentType? documentType = null,
        EInvoiceStatus? status = null,
        long? clientId = null,
        long? issuerId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets paginated, filtered and sorted invoices
    /// </summary>
    Task<PagedResult<InvoiceDto>> GetInvoicesPagedAsync(
        InvoiceFilterDto filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific invoice by ID
    /// </summary>
    /// <param name="invoiceId">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Invoice data or null if not found</returns>
    Task<InvoiceDto?> GetInvoiceByIdAsync(long invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets invoice by document number
    /// </summary>
    /// <param name="documentNumber">Document number</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Invoice data or null if not found</returns>
    Task<InvoiceDto?> GetInvoiceByDocumentNumberAsync(string documentNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new invoice or credit note
    /// Automatically generates document number if not provided
    /// Calculates totals from line items
    /// </summary>
    /// <param name="createDto">Invoice creation data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created invoice</returns>
    Task<InvoiceDto> CreateInvoiceAsync(CreateInvoiceDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing invoice
    /// Only allowed in Draft or Completed status
    /// Cannot change document type or document number once completed
    /// </summary>
    /// <param name="invoiceId">Invoice ID to update</param>
    /// <param name="updateDto">Updated data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated invoice or null if not found</returns>
    Task<InvoiceDto?> UpdateInvoiceAsync(long invoiceId, UpdateInvoiceDto updateDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks invoice as completed
    /// Generates document number if not already set
    /// Cannot be undone
    /// </summary>
    /// <param name="invoiceId">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated invoice or null if not found</returns>
    Task<InvoiceDto?> CompleteInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks invoice as paid
    /// Only possible for Completed invoices
    /// </summary>
    /// <param name="invoiceId">Invoice ID</param>
    /// <param name="paidAt">Payment date (null = now)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated invoice or null if not found</returns>
    Task<InvoiceDto?> MarkAsPaidAsync(long invoiceId, DateTime? paidAt = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft deletes an invoice
    /// Only allowed for Draft invoices
    /// </summary>
    /// <param name="invoiceId">Invoice ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if deleted, false if not found or cannot be deleted</returns>
    Task<bool> DeleteInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a soft-deleted invoice back to Draft status.
    /// Only allowed for invoices with Status == Deleted.
    /// </summary>
    /// <param name="invoiceId">Invoice ID to restore</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Restored invoice DTO, or null if not found</returns>
    Task<InvoiceDto?> RestoreInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a credit note for an existing invoice
    /// Automatically sets DocumentType = CreditNote and references original invoice
    /// </summary>
    /// <param name="originalInvoiceId">Original invoice ID</param>
    /// <param name="createDto">Credit note data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Created credit note</returns>
    Task<InvoiceDto> CreateCreditNoteAsync(long originalInvoiceId, CreateInvoiceDto createDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all credit notes for a specific invoice
    /// </summary>
    /// <param name="invoiceId">Original invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of credit notes</returns>
    Task<List<InvoiceDto>> GetCreditNotesForInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default);

    // ─── Bulk Operations ─────────────────────────────────────────────────────

    /// <summary>
    /// Completes (issues) multiple draft invoices in a single batch.
    /// Each invoice is processed individually — failures don't stop the batch.
    /// </summary>
    /// <param name="invoiceIds">List of invoice IDs to complete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result with success/failure counts and error details</returns>
    Task<BulkOperationResult> BulkCompleteAsync(List<long> invoiceIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks multiple completed invoices as paid in a single batch.
    /// Each invoice is processed individually — failures don't stop the batch.
    /// </summary>
    /// <param name="invoiceIds">List of invoice IDs to mark as paid</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result with success/failure counts and error details</returns>
    Task<BulkOperationResult> BulkMarkAsPaidAsync(List<long> invoiceIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes multiple draft invoices in a single batch.
    /// Each invoice is processed individually — failures don't stop the batch.
    /// </summary>
    /// <param name="invoiceIds">List of invoice IDs to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result with success/failure counts and error details</returns>
    Task<BulkOperationResult> BulkDeleteAsync(List<long> invoiceIds, CancellationToken cancellationToken = default);
}
