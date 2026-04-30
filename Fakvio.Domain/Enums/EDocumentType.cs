namespace Fakvio.Domain.Enums;

/// <summary>
/// Type of document issued or tracked in the system.
/// All document types share the same <c>Invoice</c> table (one-table-per-hierarchy).
/// Existing values must NEVER be renumbered — the integer is stored in the database.
/// </summary>
public enum EDocumentType
{
    /// <summary>
    /// Standard tax invoice (faktura-daňový doklad).
    /// Requests payment for goods or services rendered.
    /// </summary>
    Invoice = 1,

    /// <summary>
    /// Credit note (dobropis).
    /// Reduces or fully cancels a previously issued invoice.
    /// Used for returns, corrections, or retrospective discounts.
    /// References the corrected invoice via <c>OriginalInvoiceId</c>.
    /// </summary>
    CreditNote = 2,

    /// <summary>
    /// Pro-forma / advance invoice (zálohová faktura / proforma).
    /// A payment request sent before goods or services are delivered.
    /// NOT a tax document — it does not create a VAT obligation.
    /// When payment is received, a <see cref="TaxReceiptForAdvance"/> is issued.
    /// </summary>
    Proforma = 3,

    /// <summary>
    /// Tax receipt for advance payment (daňový doklad o přijaté platbě).
    /// Issued after the advance payment of a <see cref="Proforma"/> is received.
    /// This IS a VAT tax document and triggers the VAT obligation.
    /// References the originating pro-forma via <c>OriginalInvoiceId</c>.
    /// </summary>
    TaxReceiptForAdvance = 4
}
