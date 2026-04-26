namespace Fakvio.Domain.Enums;

/// <summary>
/// Current status of an invoice in its lifecycle
/// Flow: Draft -> Completed -> Paid
/// </summary>
public enum EInvoiceStatus
{
    /// <summary>
    /// Invoice is being created, number can still be changed
    /// Not yet finalized for sending to client
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Invoice is finalized and ready to be sent to client
    /// Number is locked and cannot be changed
    /// </summary>
    Completed = 2,

    /// <summary>
    /// Invoice has been fully paid by the client
    /// </summary>
    Paid = 3,

    /// <summary>
    /// Invoice has been cancelled using a credit note
    /// The original invoice remains in the system but is marked as credited
    /// </summary>
    Creditnoted = 4,

    /// <summary>
    /// Invoice is marked as deleted (soft delete)
    /// Still exists in database but is hidden from normal views
    /// </summary>
    Deleted = 5,

    /// <summary>
    /// At least one payment has been matched, but the invoice is not fully paid yet.
    /// PaidAmount > 0 AND PaidAmount &lt; TotalWithVat.
    /// Payment matching feature (see PLATBY-ZADANI.md).
    /// </summary>
    PartiallyPaid = 6
}
