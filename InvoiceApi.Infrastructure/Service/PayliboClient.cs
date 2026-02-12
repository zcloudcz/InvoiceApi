using InvoiceApi.Application.QrPayment;
using InvoiceApi.Application.Service;
using Microsoft.Extensions.Logging;

namespace InvoiceApi.Infrastructure.Service;

/// <summary>
/// REST client for the paylibo.com QR payment code generator.
///
/// Same approach as Monarc.Core's QRPaymentRestClient:
/// 1. Build query string from PayliboQrOptions (Czech bank account details)
/// 2. GET https://api.paylibo.com/paylibo/generator/czech/image?{queryString}
/// 3. Return PNG image bytes
///
/// The paylibo API converts Czech domestic bank account format to a valid
/// QR Platba (SPD) code that all Czech banking apps can scan.
/// </summary>
public class PayliboClient : IPayliboClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PayliboClient> _logger;

    /// <summary>
    /// Paylibo API base URL — same endpoint used by Monarc.Core.
    /// </summary>
    private const string PayliboApiUrl = "https://api.paylibo.com/paylibo/generator/czech/image";

    public PayliboClient(HttpClient httpClient, ILogger<PayliboClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<byte[]> CreateQrPaymentImageAsync(PayliboQrOptions options)
    {
        try
        {
            // Build full URL with query string (same pattern as Monarc.Core)
            var queryString = options.ToString();
            var requestUrl = $"{PayliboApiUrl}?{queryString}";

            _logger.LogInformation("Calling paylibo API for QR payment code generation");

            var response = await _httpClient.GetAsync(requestUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Paylibo API returned {StatusCode} — returning empty QR image",
                    response.StatusCode);
                return Array.Empty<byte>();
            }

            return await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex)
        {
            // Paylibo failure is non-critical — invoice can still be generated without QR
            _logger.LogWarning(ex, "Paylibo API call failed — returning empty QR image");
            return Array.Empty<byte>();
        }
    }
}
