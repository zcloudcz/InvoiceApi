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
    /// Reverts a Completed invoice back to Draft so it can be fully edited.
    /// Only Completed invoices can be reverted — Paid and Creditnoted cannot.
    /// </summary>
    Task<InvoiceDto?> RevertToDraftAsync(long invoiceId, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Previews the EU OSS destination country (ISO2) an invoice for <paramref name="clientId"/> would get,
    /// or null when it is not an OSS case. Lets the UI offer the destination country's VAT rates before saving
    /// (the server re-detects on save — this is only a hint). See DEVGUIDE §4.15.
    /// </summary>
    Task<string?> GetOssCountryCodeAsync(long clientId, EDocumentType documentType, CancellationToken cancellationToken = default);

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

    // ─── Proforma → Final Invoice ─────────────────────────────────────────────

    /// <summary>
    /// Issues a final Invoice from a Proforma (advance invoice).
    /// The final invoice contains the caller-supplied line items PLUS automatically
    /// generated deduction rows ("Odečet přijaté zálohy") — one negative row per VAT
    /// rate found on the proforma, proportional to the requested deduction amount.
    ///
    /// Supports 1:N: one proforma can have multiple final invoices, each deducting
    /// a portion of the advance. The sum of all deductions must not exceed the
    /// proforma's PaidAmount.
    ///
    /// The proforma lifecycle is NOT changed by this call (it stays Paid).
    /// </summary>
    /// <param name="proformaId">ID of the Proforma invoice to issue against</param>
    /// <param name="dto">New invoice items + optional partial deduction amount</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Newly created Invoice DTO with deduction rows already appended</returns>
    /// <exception cref="KeyNotFoundException">Proforma not found</exception>
    /// <exception cref="InvalidOperationException">
    ///   proformaId references a non-Proforma document, or deduction exceeds remaining advance
    /// </exception>
    Task<InvoiceDto> IssueFinalInvoiceAsync(
        long proformaId,
        IssueFinalInvoiceDto dto,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the remaining advance amount for a proforma:
    /// proforma.PaidAmount minus the sum of deduction rows already issued
    /// on all linked final invoices.
    /// Returns 0 if the proforma has no PaidAmount or all has been deducted.
    /// </summary>
    /// <param name="proformaId">Proforma ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<decimal> GetRemainingAdvanceAsync(long proformaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all final Invoices (DocumentType=Invoice) that were issued from the given proforma.
    /// Identified by OriginalInvoiceId = proformaId and DocumentType = Invoice.
    /// Excludes Deleted documents.
    /// </summary>
    /// <param name="proformaId">Proforma ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<InvoiceDto>> GetFinalInvoicesForProformaAsync(long proformaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all TaxReceiptForAdvance documents linked to the given proforma.
    /// Identified by OriginalInvoiceId = proformaId and DocumentType = TaxReceiptForAdvance.
    /// Excludes Deleted documents.
    /// </summary>
    /// <param name="proformaId">Proforma ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<InvoiceDto>> GetTaxReceiptsForProformaAsync(long proformaId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts a Paid invoice back to Completed status (marks it as unpaid).
    /// Unlinks any PaymentMatch records by deleting them and recalculates the invoice.
    /// Only Paid invoices can be marked as unpaid — other statuses throw InvalidOperationException.
    /// </summary>
    /// <param name="invoiceId">Invoice ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated invoice DTO</returns>
    /// <exception cref="KeyNotFoundException">Invoice not found</exception>
    /// <exception cref="InvalidOperationException">Invoice is not in Paid status</exception>
    Task<InvoiceDto> MarkAsUnpaidAsync(long invoiceId, CancellationToken cancellationToken = default);

    // ─── Copy ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new Draft invoice as an exact copy of the source document.
    ///
    /// What is copied: client, issuer, items (deep copy — new InvoiceItem without Id/InvoiceId),
    /// currency, payment method, bank account details, notes, number sequence.
    ///
    /// What is reset (same as a brand-new invoice):
    ///   - Status = Draft
    ///   - DocumentNumber — fresh number from number sequence pipeline
    ///   - VariableSymbol — derived from the new DocumentNumber (digits only, max 10)
    ///   - IssueDate = today (UTC)
    ///   - DueDate = recalculated from client BillingSettings
    ///   - PaidAt = null, PaidAmount = 0
    ///   - IsSentByEmail = false, LastSentByEmailAt = null
    ///   - OriginalInvoiceId = null (copy is NOT a credit note or linked document)
    ///
    /// Supported source types: Invoice, Proforma, TaxReceiptForAdvance.
    /// CreditNote sources are rejected with <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <param name="sourceId">ID of the invoice to copy from</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Newly created Draft invoice DTO</returns>
    /// <exception cref="KeyNotFoundException">Source invoice not found</exception>
    /// <exception cref="InvalidOperationException">Source is a CreditNote — copying is not allowed</exception>
    Task<InvoiceDto> CopyInvoiceAsync(long sourceId, CancellationToken cancellationToken = default);
}
