namespace Fakvio.Application.Exceptions;

/// <summary>
/// Thrown by <see cref="Fakvio.Application.Service.IQrPaymentService.GenerateQrCodeImageAsync"/>
/// when an invoice has no usable payment destination — no IBAN, no Czech domestic account number
/// (or both fail their checksum) — to generate a payment QR code from (issue #154).
///
/// Before this exception existed, the service silently generated a SIND-only "QR Faktura": a
/// QR code that LOOKS like a payment code but carries no payment instructions at all, and
/// nothing on the printed invoice told the reader that. The controller catches this and returns
/// HTTP 400 with an actionable message instead of a decorative QR code.
/// </summary>
public sealed class NoUsableBankConnectionException : Exception
{
    /// <summary>The invoice that has no usable IBAN or Czech account number.</summary>
    public long InvoiceId { get; }

    public NoUsableBankConnectionException(long invoiceId)
        : base($"Invoice {invoiceId} has no usable bank connection " +
               "(no valid IBAN or Czech account number) — QR code was not generated.")
    {
        InvoiceId = invoiceId;
    }
}
