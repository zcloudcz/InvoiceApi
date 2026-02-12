namespace InvoiceApi.Domain.Enums;

/// <summary>
/// Type of document - Invoice or Credit Note
/// Credit notes are used to cancel or reduce the amount of an existing invoice
/// </summary>
public enum EDocumentType
{
    /// <summary>
    /// Standard invoice - a document requesting payment for goods or services
    /// </summary>
    Invoice = 1,

    /// <summary>
    /// Credit note - a document that reduces or cancels an existing invoice
    /// Used when returning goods, correcting errors, or providing discounts
    /// </summary>
    CreditNote = 2
}
