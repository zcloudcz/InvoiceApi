namespace Fakvio.Application.Service;

/// <summary>
/// Issues a tax receipt for advance payment (daňový doklad o přijaté platbě / DPP) from a
/// paid pro-forma invoice.
///
/// The caller decides WHEN to invoke this (based on EAdvanceTaxReceiptMode):
///   - PaymentMatchingService calls it for OnPaymentMatch and OnAnyPayment modes
///     when a pro-forma reaches Paid status via the automatic bank-matching pipeline.
///   - InvoiceService.MarkAsPaidAsync calls it for the OnAnyPayment mode when
///     the user manually marks a pro-forma as paid.
///   - Disabled mode: neither calls it.
///
/// The service is idempotent — calling it twice for the same pro-forma does NOT
/// create two DPP documents. If a TaxReceiptForAdvance already exists for the
/// given proformaInvoiceId, the method returns the existing one without side effects.
/// </summary>
public interface IAdvanceTaxReceiptService
{
    /// <summary>
    /// Creates and completes a TaxReceiptForAdvance document for the given pro-forma invoice.
    ///
    /// The new document:
    ///   - DocumentType  = TaxReceiptForAdvance
    ///   - OriginalInvoiceId = proformaInvoiceId
    ///   - IssueDate     = paymentDate
    ///   - TaxableSupplyDate (DUZP) = paymentDate
    ///   - VariableSymbol = same as the pro-forma
    ///   - Status        = Completed
    ///   - InvoiceItems  = one item per VAT rate, split proportionally from the pro-forma
    ///                     so that paidAmount maps back to the correct VAT distribution.
    ///
    /// Overpayment (paidAmount &gt; proforma.TotalWithVat):
    ///   The DPP is issued for the full paidAmount. The HasAlert flag on the returned
    ///   document is set to true so the user can see there was an overpayment.
    ///
    /// Idempotence: when a TaxReceiptForAdvance already exists for this pro-forma,
    ///   the existing document is returned and no new document is created.
    /// </summary>
    /// <param name="proformaInvoiceId">ID of the paid pro-forma invoice.</param>
    /// <param name="paidAmount">The amount actually received (from PaymentMatch or manual mark-paid).</param>
    /// <param name="paymentDate">Date when the payment was received — becomes IssueDate and DUZP.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///   ID of the created (or already-existing) TaxReceiptForAdvance invoice,
    ///   or null when the pro-forma is not found or not a Proforma document type.
    /// </returns>
    Task<long?> IssueFromPaidProformaAsync(
        long proformaInvoiceId,
        decimal paidAmount,
        DateTime paymentDate,
        CancellationToken ct = default);
}
