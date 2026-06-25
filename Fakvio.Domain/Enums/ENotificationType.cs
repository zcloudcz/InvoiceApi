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
    PaymentMatched = 1
}
