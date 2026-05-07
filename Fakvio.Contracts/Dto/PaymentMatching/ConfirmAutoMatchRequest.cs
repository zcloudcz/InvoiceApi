namespace Fakvio.Contracts.Dto.PaymentMatching;

/// <summary>
/// Request body sent by the UI when the user clicks "Confirm" in the auto-match dialog.
/// Exactly one of InvoiceId / ReceivedInvoiceId must be set.
/// </summary>
public class ConfirmAutoMatchRequest
{
    /// <summary>The bank transaction that was proposed as a match.</summary>
    public long BankTransactionId { get; set; }

    /// <summary>Issued invoice to match against (null if matching a received invoice).</summary>
    public long? InvoiceId { get; set; }

    /// <summary>Received (supplier) invoice to match against (null if matching an issued invoice).</summary>
    public long? ReceivedInvoiceId { get; set; }
}
