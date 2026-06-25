namespace Fakvio.Domain.Enums;

/// <summary>
/// Type of in-app notification. Extend this enum when adding new notification
/// triggers — no schema change required.
/// </summary>
public enum ENotificationType
{
    /// <summary>
    /// A bank transaction was matched to an invoice (auto or manual).
    /// Created by PaymentMatchingService after a successful match.
    /// </summary>
    PaymentMatched = 1,

    /// <summary>
    /// An invoice was automatically imported from an inbound email.
    /// Created by InvoiceEmailProcessor after successful import.
    /// </summary>
    InvoiceEmailImported = 2,

    /// <summary>
    /// An inbound invoice email was imported but flagged for review (low AI confidence).
    /// Created by InvoiceEmailProcessor when classification confidence is below threshold.
    /// </summary>
    InvoiceEmailNeedsReview = 3
}
