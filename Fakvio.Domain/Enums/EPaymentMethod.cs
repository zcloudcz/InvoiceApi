namespace Fakvio.Domain.Enums;

/// <summary>
/// Defines available payment methods for invoices and billing settings.
/// Stored as integer in the database for consistency and type safety.
/// Display names are localized via resource keys: "EPaymentMethod_{Value}"
/// </summary>
public enum EPaymentMethod
{
    /// <summary>
    /// Payment via bank wire transfer (most common in CZ/EU)
    /// </summary>
    BankTransfer = 1,

    /// <summary>
    /// Cash payment on delivery or in person
    /// </summary>
    Cash = 2,

    /// <summary>
    /// Payment by credit or debit card
    /// </summary>
    CreditCard = 3,

    /// <summary>
    /// Payment via PayPal online service
    /// </summary>
    PayPal = 4,

    /// <summary>
    /// Any other payment method not listed above
    /// </summary>
    Other = 5
}
