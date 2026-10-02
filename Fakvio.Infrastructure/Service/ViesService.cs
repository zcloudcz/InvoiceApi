using System.Net.Http.Json;
using System.Text.Json;
using Fakvio.Application.Service;
using Fakvio.Contracts.Dto.Vies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Calls the European Commission's VIES REST API to verify an EU VAT identification number.
///
/// API: POST https://ec.europa.eu/taxation_customs/vies/rest-api/check-vat-number
/// Body: {"countryCode":"CZ","vatNumber":"12345678"}
///
/// The REST service answers HTTP 200 even when the number is invalid — the outcome is carried
/// in the body's "valid"/"userError" fields, not the status code. "userError" also reports
/// service-level failures (MS_UNAVAILABLE, TIMEOUT, SERVICE_UNAVAILABLE, MS_MAX_CONCURRENT_REQ,
/// GLOBAL_MAX_CONCURRENT_REQ) — those must surface as "could not verify", never as "invalid DIČ",
/// because the number itself was never actually checked.
/// </summary>
public class ViesService : IViesService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ViesService> _logger;

    private const string ApiUrl = "https://ec.europa.eu/taxation_customs/vies/rest-api/check-vat-number";

    // Only successful ("Valid") lookups are cached — an "Invalid" or "Unavailable" result must
    // stay retryable immediately (a VIES outage, or a company that registers for VAT minutes later).
    private static readonly TimeSpan ValidCacheExpiration = TimeSpan.FromHours(24);

    // VIES "userError" codes that mean the SERVICE failed, not that the number is invalid.
    private static readonly HashSet<string> ServiceErrorCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MS_UNAVAILABLE", "TIMEOUT", "SERVICE_UNAVAILABLE", "MS_MAX_CONCURRENT_REQ", "GLOBAL_MAX_CONCURRENT_REQ",
        "GLOBAL_MAX_CONCURRENT_REQ_TIME", "MS_MAX_CONCURRENT_REQ_TIME"
    };

    public ViesService(HttpClient httpClient, IMemoryCache cache, ILogger<ViesService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ViesVerificationResult> VerifyAsync(string vatId, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(vatId, out var countryCode, out var vatNumber))
        {
            // Malformed input never reaches VIES — not worth a round-trip and not cacheable
            // (the caller is expected to fix the input, not retry the same string).
            _logger.LogInformation("VIES verification skipped — malformed VAT ID format");
            return new ViesVerificationResult
            {
                Status = EViesCheckStatus.Invalid,
                CountryCode = string.Empty,
                VatNumber = vatId?.Trim() ?? string.Empty,
                ErrorMessage = "Malformed VAT ID — expected a 2-letter country code followed by the number (e.g. CZ12345678)."
            };
        }

        var cacheKey = $"vies:{countryCode}{vatNumber}";
        if (_cache.TryGetValue(cacheKey, out ViesVerificationResult? cached) && cached is not null)
        {
            _logger.LogInformation("VIES verification for {CountryCode} served from cache", countryCode);
            return cached;
        }

        try
        {
            _logger.LogInformation("Verifying VAT ID via VIES for country {CountryCode}", countryCode);

            // No PII beyond the country code is logged anywhere in this method — the VAT number
            // itself is only ever sent to VIES, never written to a log line.
            using var response = await _httpClient.PostAsJsonAsync(
                ApiUrl,
                new { countryCode, vatNumber },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("VIES API returned HTTP {StatusCode} for country {CountryCode}",
                    response.StatusCode, countryCode);
                return Unavailable(countryCode, vatNumber, $"VIES service returned HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken);
            var result = ParseResponse(json, countryCode, vatNumber);

            if (result.Status == EViesCheckStatus.Valid)
            {
                _cache.Set(cacheKey, result, ValidCacheExpiration);
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as a (non-caller-requested) OperationCanceledException.
            _logger.LogWarning("VIES API call timed out for country {CountryCode}", countryCode);
            return Unavailable(countryCode, vatNumber, "VIES service did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "VIES API call failed for country {CountryCode}", countryCode);
            return Unavailable(countryCode, vatNumber, $"VIES service unavailable: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "VIES API returned an unparseable response for country {CountryCode}", countryCode);
            return Unavailable(countryCode, vatNumber, "VIES service returned an unexpected response.");
        }
    }

    /// <summary>
    /// Parses the VIES response body. Tolerates both the current "valid" boolean field and the
    /// older "isValid" spelling some VIES API versions/mirrors used.
    /// </summary>
    private static ViesVerificationResult ParseResponse(JsonElement root, string countryCode, string vatNumber)
    {
        var userError = root.TryGetProperty("userError", out var ueProp) ? ueProp.GetString() : null;

        if (!string.IsNullOrEmpty(userError) && ServiceErrorCodes.Contains(userError))
        {
            return Unavailable(countryCode, vatNumber, $"VIES could not complete the check ({userError}).");
        }

        var isValid =
            (root.TryGetProperty("valid", out var validProp) && IsTrue(validProp)) ||
            (root.TryGetProperty("isValid", out var isValidProp) && IsTrue(isValidProp));

        DateTime? requestDate = root.TryGetProperty("requestDate", out var dateProp)
            && DateTime.TryParse(dateProp.GetString(), out var parsedDate)
                ? parsedDate
                : null;

        return new ViesVerificationResult
        {
            Status = isValid ? EViesCheckStatus.Valid : EViesCheckStatus.Invalid,
            CountryCode = countryCode,
            VatNumber = vatNumber,
            Name = CleanUnknown(root, "name"),
            Address = CleanUnknown(root, "address"),
            RequestDate = requestDate,
            ErrorMessage = isValid ? null : (userError ?? "VAT ID is not registered in VIES.")
        };
    }

    private static bool IsTrue(JsonElement element) =>
        element.ValueKind == JsonValueKind.True || (element.ValueKind == JsonValueKind.String
            && bool.TryParse(element.GetString(), out var b) && b);

    /// <summary>
    /// VIES returns the placeholder "---" for name/address when the member state does not
    /// release that data — normalize it to null instead of showing "---" in the UI.
    /// </summary>
    private static string? CleanUnknown(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        var value = prop.GetString();
        return string.IsNullOrWhiteSpace(value) || value == "---" ? null : value;
    }

    private static ViesVerificationResult Unavailable(string countryCode, string vatNumber, string message) => new()
    {
        Status = EViesCheckStatus.Unavailable,
        CountryCode = countryCode,
        VatNumber = vatNumber,
        ErrorMessage = message
    };

    /// <summary>
    /// Normalizes a user-entered VAT ID ("CZ12345678", "DE 123 456 789", lowercase, dashes…)
    /// into (countryCode, vatNumber). Greece is normalized from the ISO code "GR" to the "EL"
    /// prefix VIES actually expects — the one well-known exception to "VIES code == ISO code".
    /// </summary>
    private static bool TryNormalize(string? vatId, out string countryCode, out string vatNumber)
    {
        countryCode = string.Empty;
        vatNumber = string.Empty;

        if (string.IsNullOrWhiteSpace(vatId))
        {
            return false;
        }

        var cleaned = new string(vatId.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();

        if (cleaned.Length < 3 || !char.IsLetter(cleaned[0]) || !char.IsLetter(cleaned[1]))
        {
            return false;
        }

        var prefix = cleaned[..2];
        var number = cleaned[2..];

        if (string.IsNullOrWhiteSpace(number))
        {
            return false;
        }

        countryCode = prefix == "GR" ? "EL" : prefix;
        vatNumber = number;
        return true;
    }
}
