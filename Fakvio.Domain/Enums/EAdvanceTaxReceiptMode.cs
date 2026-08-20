namespace Fakvio.Domain.Enums;

/// <summary>
/// Controls when the system automatically converts an advance invoice (proforma)
/// into a tax receipt for advance payment (daňový doklad o přijaté platbě, "DPP").
///
/// The value is stored per tenant on the issuer's <see cref="Entities.BillingSettings"/>,
/// so every company can pick the behaviour that fits its accounting workflow.
/// Customer (non-issuer) billing settings ignore this value.
///
/// NOTE for juniors: this enum only *declares* the tenant's preference. The component
/// that reads it and actually issues the DPP is not part of this change — see the
/// "Notes" in DEVGUIDE §4.7. Until it lands, the value is stored and read back but
/// nothing acts on it.
/// </summary>
public enum EAdvanceTaxReceiptMode
{
    /// <summary>
    /// Auto-conversion is disabled.
    /// The user issues the tax receipt manually from the proforma detail page.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Auto-conversion is triggered when a bank payment is automatically matched
    /// to the advance invoice by the IMAP payment-matching pipeline.
    /// This is the default for new and existing tenants.
    /// </summary>
    OnPaymentMatch = 1,

    /// <summary>
    /// Auto-conversion is triggered whenever ANY payment is recorded against the
    /// advance invoice — matched automatically or marked as paid by hand.
    /// </summary>
    OnAnyPayment = 2
}
