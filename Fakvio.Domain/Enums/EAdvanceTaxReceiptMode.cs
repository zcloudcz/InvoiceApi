namespace Fakvio.Domain.Enums;

/// <summary>
/// Controls when the system automatically converts an advance invoice (proforma)
/// into a tax receipt for advance payment (daňový doklad k přijaté platbě).
///
/// This setting is stored per-tenant (in BillingSettings on the issuer Client record)
/// so each company can choose the behaviour that fits their workflow.
///
/// Default at migration: OnPaymentMatch — matches the previously implicit behaviour
/// where payment matching triggered the conversion.
/// </summary>
public enum EAdvanceTaxReceiptMode
{
    /// <summary>
    /// Auto-conversion is disabled.
    /// The user must manually issue the tax receipt after receiving payment.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Auto-conversion is triggered when a bank payment is automatically matched
    /// to the advance invoice via the IMAP payment-matching pipeline.
    /// This is the default for existing tenants (matches the historical implicit behaviour).
    /// </summary>
    OnPaymentMatch = 1,

    /// <summary>
    /// Auto-conversion is triggered whenever ANY payment is recorded against the advance
    /// invoice — whether matched automatically or marked as paid manually by the user.
    /// </summary>
    OnAnyPayment = 2
}
