namespace Fakvio.Domain.Enums;

/// <summary>
/// Status lifecycle for received (incoming) invoices.
/// Flow: Received -> Approved -> Paid -> Deleted
/// Simpler than issued invoices — no "Draft" or "Completed" because
/// the supplier already finalized the document.
///
/// Contract/DTO enum only: received invoices are stored as <c>Invoice</c> rows with the unified
/// <see cref="EInvoiceStatus"/> (see ReceivedInvoiceService). Never persist or copy these numbers
/// (Rejected = 4 would collide with Creditnoted = 4); <c>Rejected</c> is abolished and no longer produced.
/// </summary>
public enum EReceivedInvoiceStatus
{
    /// <summary>
    /// Invoice received from supplier, not yet reviewed.
    /// Can still be edited or rejected.
    /// </summary>
    Received = 1,

    /// <summary>
    /// Invoice reviewed and approved for payment.
    /// Amounts verified, VAT checked, ready to pay.
    /// </summary>
    Approved = 2,

    /// <summary>
    /// Invoice has been paid to the supplier.
    /// </summary>
    Paid = 3,

    /// <summary>
    /// Invoice rejected (e.g., incorrect amounts, duplicate).
    /// Soft delete — still in database but hidden from normal views.
    /// </summary>
    Rejected = 4,

    /// <summary>
    /// Soft delete — hidden from normal views.
    /// </summary>
    Deleted = 5
}
