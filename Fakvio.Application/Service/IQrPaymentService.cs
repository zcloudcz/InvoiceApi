namespace Fakvio.Application.Service;

/// <summary>
/// Service for generating QR payment codes for invoices.
/// Implements the Czech QR Faktura (SIND) and QR Platba (SPD) standards.
///
/// QR Faktura = structured invoice data encoded in QR code (SIND format)
/// QR Platba = payment instruction encoded in QR code (SPD format)
/// Combined = single QR code with both payment and invoice data (SPD + X-INV)
///
/// Reference: https://www.kdpcr.cz/informace/qr-faktura
/// </summary>
public interface IQrPaymentService
{
    /// <summary>
    /// Generates the SIND (Short Invoice Descriptor) string for an invoice.
    /// This is the raw QR Faktura data string before QR code encoding.
    /// Format: SID*1.0*ID:{docNumber}*DD:{issueDate}*AM:{amount}*...
    /// </summary>
    /// <param name="invoiceId">Invoice ID to generate SIND for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>SIND string ready for QR encoding</returns>
    Task<string> GenerateSindStringAsync(long invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the SPD (Short Payment Descriptor) string with integrated QR Faktura.
    /// This combines QR Platba payment data with QR Faktura invoice data.
    /// Format: SPD*1.0*ACC:{iban}*AM:{amount}*CC:{currency}*X-INV:{encoded_sind}*...
    /// </summary>
    /// <param name="invoiceId">Invoice ID to generate combined QR for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>SPD string with integrated SIND (X-INV), ready for QR encoding</returns>
    Task<string> GenerateSpdWithInvoiceAsync(long invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a QR code image (PNG) for the given invoice's payment — never a decorative,
    /// non-payable code (issue #154).
    /// If the invoice has a checksum-valid IBAN, generates QR Platba (SPD) via local generation.
    /// Otherwise, if it has a checksum-valid Czech domestic account number, generates QR Platba
    /// via the paylibo.com API.
    /// If neither is present and valid, throws <see cref="Fakvio.Application.Exceptions.NoUsableBankConnectionException"/> —
    /// no QR code is generated.
    /// </summary>
    /// <param name="invoiceId">Invoice ID to generate QR image for</param>
    /// <param name="pixelsPerModule">Size of each QR module in pixels (default 10)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>PNG image bytes of the QR code</returns>
    /// <exception cref="Fakvio.Application.Exceptions.NoUsableBankConnectionException">
    /// The invoice has no IBAN or Czech account number that passes its checksum.
    /// </exception>
    Task<byte[]> GenerateQrCodeImageAsync(long invoiceId, int pixelsPerModule = 10, CancellationToken cancellationToken = default);
}
