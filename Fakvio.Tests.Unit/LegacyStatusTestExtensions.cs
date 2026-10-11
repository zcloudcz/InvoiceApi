using Fakvio.Domain.Enums;

namespace Fakvio.Tests.Unit;

/// <summary>
/// Test helper: received invoices are stored as <c>Invoice</c> rows, so seeds need the stored status.
/// Lets the older tests keep speaking the legacy <see cref="EReceivedInvoiceStatus"/> vocabulary.
/// </summary>
internal static class LegacyStatusTestExtensions
{
    /// <summary>Received -> Draft, Approved -> Completed, Paid -> Paid, Rejected and Deleted -> Deleted.</summary>
    public static EInvoiceStatus ToStored(this EReceivedInvoiceStatus status) => status switch
    {
        EReceivedInvoiceStatus.Received => EInvoiceStatus.Draft,
        EReceivedInvoiceStatus.Approved => EInvoiceStatus.Completed,
        EReceivedInvoiceStatus.Paid => EInvoiceStatus.Paid,
        _ => EInvoiceStatus.Deleted // Rejected is abolished, Deleted stays Deleted
    };
}
