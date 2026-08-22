namespace Fakvio.Application.QrPayment;

/// <summary>
/// Why a QR payment code could not be produced. The caller uses this to decide what to
/// tell the user and, in the API, which HTTP status to return.
/// </summary>
public enum EQrPaymentUnavailableReason
{
    /// <summary>
    /// The document carries no bank connection at all (no IBAN, no account number),
    /// or none that the requested format can use. The user has to fix the configuration.
    /// </summary>
    NoBankAccount = 0,

    /// <summary>
    /// A bank connection is present but it is not valid (failed IBAN or modulo 11 check),
    /// so any QR code built from it would be unscannable or unpayable.
    /// The user has to correct the value.
    /// </summary>
    InvalidBankAccount = 1,

    /// <summary>
    /// The external QR generator (paylibo.com) could not be reached or returned garbage.
    /// Nothing is misconfigured — this is a transient operator/infrastructure problem.
    /// </summary>
    ProviderUnavailable = 2
}

/// <summary>
/// Thrown when a payable QR code cannot be produced for a document.
///
/// WHY this exception exists (issue #154):
/// the previous implementation never failed. With no usable bank connection it emitted a
/// SIND-only "QR Faktura" — a QR code that looks like a payment code, scans fine, and then
/// does nothing in a banking app, because it carries no payment instructions. Neither the
/// issuer nor the recipient had any way of telling it apart from a real QR Platba; the only
/// trace was a LogInformation line nobody reads.
///
/// A QR code that cannot be paid is worse than no QR code, so the generator now refuses to
/// produce one and says why. The message is written for the end user: it names what is
/// missing or wrong and where to fix it, following the same convention as the number
/// sequence errors (issue #155).
/// </summary>
public class QrPaymentUnavailableException : Exception
{
    /// <summary>
    /// Machine-readable classification of the failure — see <see cref="EQrPaymentUnavailableReason"/>.
    /// </summary>
    public EQrPaymentUnavailableReason Reason { get; }

    /// <summary>
    /// Id of the document the QR code was requested for. Kept for logging and diagnostics.
    /// </summary>
    public long InvoiceId { get; }

    public QrPaymentUnavailableException(
        EQrPaymentUnavailableReason reason,
        long invoiceId,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        InvoiceId = invoiceId;
    }
}
