namespace Fakvio.Application.Exceptions;

/// <summary>
/// Thrown when an EPO export is attempted for a company that is not registered as a VAT payer.
///
/// Only companies with <c>IsVatPayer = true</c> can submit DPHDP3 or DPHKH1 filings
/// to the Czech Tax Authority portal (EPO). Issuing an EPO export for a non-payer would
/// produce a file that the portal would reject, so we block the request early.
///
/// The controller catches this and converts it to HTTP 403 Forbidden
/// with machine-readable code <c>VAT_PAYER_REQUIRED</c>.
/// </summary>
public sealed class VatPayerRequiredException : Exception
{
    /// <summary>
    /// Initializes a new instance with a fixed descriptive message.
    /// </summary>
    public VatPayerRequiredException()
        : base(
            "The company is not registered as a VAT payer (IsVatPayer = false). " +
            "EPO exports (DPHDP3 / DPHKH1) are only available to VAT payers.")
    {
    }
}
