namespace Fakvio.Domain.Enums;

/// <summary>
/// Direction of an invoice received by email — determined by comparing
/// issuer/recipient IČO against the tenant's company.
/// </summary>
public enum EInvoiceDirection
{
    /// <summary>Supplier sent us an invoice — creates a ReceivedInvoice.</summary>
    Received = 1,

    /// <summary>Confirmation/copy of our own issued invoice — creates an Invoice.</summary>
    Issued = 2
}
