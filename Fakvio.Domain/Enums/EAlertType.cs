namespace Fakvio.Domain.Enums;

/// <summary>
/// Type of business alert. Designed to be extended without schema changes —
/// simply add a new value here and create the corresponding alert in the
/// service that detects the condition.
/// </summary>
public enum EAlertType
{
    /// <summary>
    /// A payment received against a proforma (advance invoice) exceeded the
    /// proforma's total amount. The user must decide whether to return the
    /// overpayment, apply it to another document, or issue a corrected receipt.
    /// Created by AdvanceTaxReceiptService (issue #5) when PaidAmount > TotalWithVat.
    /// </summary>
    OverpaidProforma = 1
}
