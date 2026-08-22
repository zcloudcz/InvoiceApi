using Fakvio.Application.QrPayment;
using Fakvio.Application.Service;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

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

    /// <summary>
    /// Media type prefix every successful paylibo response must carry.
    /// The API answers an unparsable request with a 200 and an HTML/JSON error body,
    /// which would otherwise be handed on as if it were a PNG.
    /// </summary>
    private const string ImageMediaTypePrefix = "image/";

    /// <inheritdoc />
    public async Task<byte[]> CreateQrPaymentImageAsync(PayliboQrOptions options)
    {
        // Build full URL with query string (same pattern as Monarc.Core)
        var queryString = options.ToString();
        var requestUrl = $"{PayliboApiUrl}?{queryString}";

        _logger.LogInformation("Calling paylibo API for QR payment code generation");

        var response = await _httpClient.GetAsync(requestUrl);

        // Issue #154: every failure used to be swallowed into an empty byte array, and the
        // caller silently replaced the missing payment code with a decorative one. Failures
        // are propagated now; QrPaymentService turns them into a ProviderUnavailable error
        // that the operator sees in the log and the user sees in the UI.
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.StartsWith(ImageMediaTypePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(
                $"Paylibo API returned content type '{mediaType ?? "(none)"}' instead of an image.");
        }

        var image = await response.Content.ReadAsByteArrayAsync();
        if (image.Length == 0)
        {
            throw new HttpRequestException("Paylibo API returned an empty image body.");
        }

        return image;
    }
}
