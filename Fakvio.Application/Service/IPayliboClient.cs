using Fakvio.Application.QrPayment;

namespace Fakvio.Application.Service;

/// <summary>
/// REST client for the paylibo.com QR payment code generator API.
///
/// Paylibo generates valid Czech QR Platba (SPD) payment codes from
/// Czech domestic bank account format (accountPrefix-accountNumber/bankCode).
///
/// This is the same approach used by Monarc.Core — the API handles the
/// conversion from Czech bank account format to valid SPD QR code internally.
///
/// API: https://api.paylibo.com/paylibo/generator/czech/image
/// </summary>
public interface IPayliboClient
{
    /// <summary>
    /// Calls the paylibo API to generate a QR payment code PNG image.
    /// Returns empty byte array if the API call fails.
    /// </summary>
    /// <param name="options">Payment options (bank account, amount, VS, etc.)</param>
    /// <returns>PNG image bytes, or empty array on failure</returns>
    Task<byte[]> CreateQrPaymentImageAsync(PayliboQrOptions options);
}
