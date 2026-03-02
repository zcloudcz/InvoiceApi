using System.Text.Json;
using AresService.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AresService;

/// <summary>
/// Implementation of ARES service
/// Fetches company data from Czech business registry and caches results
/// </summary>
public class AresServiceImpl : IAresService
{
    private readonly HttpClient _httpClient;
    private readonly IAresCacheRepository _cacheRepository;
    private readonly ILogger<AresServiceImpl> _logger;

    // ARES API base URL — read from configuration (AresSettings:BaseUrl).
    // Falls back to the official government registry URL if not configured.
    private readonly string _aresApiBaseUrl;

    // Default ARES API URL — used when AresSettings:BaseUrl is not set in configuration.
    private const string DefaultAresApiBaseUrl = "https://ares.gov.cz/ekonomicke-subjekty-v-be/rest/ekonomicke-subjekty";

    // Cache validity period — successful lookups last 30 days,
    // failed lookups only 1 hour (so retries aren't blocked for long).
    private static readonly TimeSpan SuccessCacheExpiration = TimeSpan.FromDays(30);
    private static readonly TimeSpan FailureCacheExpiration = TimeSpan.FromHours(1);

    public AresServiceImpl(
        HttpClient httpClient,
        IAresCacheRepository cacheRepository,
        ILogger<AresServiceImpl> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _cacheRepository = cacheRepository;
        _logger = logger;

        // Read ARES base URL from config — allows overriding in appsettings.json,
        // local.settings.json, or Azure App Settings (AresSettings__BaseUrl).
        _aresApiBaseUrl = configuration["AresSettings:BaseUrl"] ?? DefaultAresApiBaseUrl;
    }

    /// <summary>
    /// Gets company information from cache or ARES API.
    /// Checks cache first, then fetches from API if needed.
    /// Cache failures are non-fatal — if the database is unavailable
    /// (e.g., tenant not provisioned), we skip the cache and fetch directly from ARES.
    /// </summary>
    public async Task<AresCompanyInfo> GetCompanyInfoAsync(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        // Validate input - IČO should be 8 digits
        if (string.IsNullOrWhiteSpace(registrationNumber) || registrationNumber.Length != 8)
        {
            _logger.LogWarning("Invalid registration number format: {RegistrationNumber}", registrationNumber);
            return new AresCompanyInfo
            {
                RegistrationNumber = registrationNumber,
                IsSuccessful = false,
                ErrorMessage = "Registration number must be exactly 8 digits",
                FetchedAt = DateTime.UtcNow
            };
        }

        // Try to get from cache first.
        // Cache access is wrapped in try/catch because the underlying database
        // might not be available (e.g., tenant DB not yet provisioned, SQL Server
        // connection error). In that case, we skip the cache and fetch from ARES.
        try
        {
            var cachedData = await _cacheRepository.GetCachedDataAsync(registrationNumber, cancellationToken);

            if (cachedData != null && cachedData.ExpiresAt > DateTime.UtcNow)
            {
                _logger.LogInformation("Using cached data for IČO {RegistrationNumber}", registrationNumber);
                return DeserializeCachedData(cachedData);
            }
        }
        catch (Exception ex)
        {
            // Cache is a nice-to-have optimization, not a hard requirement.
            // If the DB is down or the table doesn't exist, just skip cache and fetch live.
            _logger.LogWarning(ex,
                "Cache lookup failed for IČO {RegistrationNumber} — fetching directly from ARES",
                registrationNumber);
        }

        // Cache miss, expired, or unavailable — fetch from ARES
        _logger.LogInformation("Fetching IČO {RegistrationNumber} from ARES", registrationNumber);
        return await RefreshCompanyInfoAsync(registrationNumber, cancellationToken);
    }

    /// <summary>
    /// Forces refresh from ARES API, bypassing cache
    /// </summary>
    public async Task<AresCompanyInfo> RefreshCompanyInfoAsync(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Call ARES API
            var url = $"{_aresApiBaseUrl}/{registrationNumber}";
            _logger.LogInformation("Fetching company data from ARES: {Url}", url);

            var response = await _httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ARES API returned error status: {StatusCode} for IČO {RegistrationNumber}",
                    response.StatusCode, registrationNumber);

                var errorResult = new AresCompanyInfo
                {
                    RegistrationNumber = registrationNumber,
                    IsSuccessful = false,
                    ErrorMessage = $"Company not found in registry (HTTP {response.StatusCode})",
                    FetchedAt = DateTime.UtcNow
                };

                // Cache the failed lookup to avoid repeated API calls
                await CacheResult(errorResult, cancellationToken);
                return errorResult;
            }

            var jsonContent = await response.Content.ReadAsStringAsync(cancellationToken);

            // Parse ARES response
            var companyInfo = ParseAresResponse(jsonContent, registrationNumber);

            // Cache the successful result
            await CacheResult(companyInfo, cancellationToken);

            _logger.LogInformation("Successfully fetched and cached data for IČO {RegistrationNumber}", registrationNumber);

            return companyInfo;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "HTTP error while fetching data from ARES for IČO {RegistrationNumber}", registrationNumber);
            return new AresCompanyInfo
            {
                RegistrationNumber = registrationNumber,
                IsSuccessful = false,
                ErrorMessage = $"ARES API connection error: {ex.Message}",
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while fetching data from ARES for IČO {RegistrationNumber}", registrationNumber);
            return new AresCompanyInfo
            {
                RegistrationNumber = registrationNumber,
                IsSuccessful = false,
                ErrorMessage = $"Unexpected error: {ex.Message}",
                FetchedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Checks if valid cached data exists
    /// </summary>
    public async Task<bool> IsCachedAsync(
        string registrationNumber,
        CancellationToken cancellationToken = default)
    {
        var cachedData = await _cacheRepository.GetCachedDataAsync(registrationNumber, cancellationToken);
        return cachedData != null && cachedData.ExpiresAt > DateTime.UtcNow && cachedData.IsSuccessful;
    }

    /// <summary>
    /// Parses JSON response from ARES API into our model
    /// </summary>
    private AresCompanyInfo ParseAresResponse(string jsonContent, string registrationNumber)
    {
        try
        {
            using var document = JsonDocument.Parse(jsonContent);
            var root = document.RootElement;

            // Extract company name
            var companyName = root.GetProperty("obchodniJmeno").GetString() ?? string.Empty;

            // Extract tax number (DIČ) if available
            string? taxNumber = null;
            var isVatPayer = false;

            if (root.TryGetProperty("dic", out var dicElement))
            {
                taxNumber = dicElement.GetString();
                isVatPayer = !string.IsNullOrEmpty(taxNumber);
            }

            // Extract address
            AresAddress? address = null;
            if (root.TryGetProperty("sidlo", out var addressElement))
            {
                address = new AresAddress
                {
                    Street = GetAddressString(addressElement),
                    City = addressElement.TryGetProperty("nazevObce", out var city)
                        ? city.GetString() ?? string.Empty
                        : string.Empty,
                    PostalCode = addressElement.TryGetProperty("psc", out var psc)
                        ? FormatPostalCode(psc.GetInt32())
                        : string.Empty,
                    Country = "Česká republika"
                };
            }

            // Extract legal form - ARES returns it as a plain string code (e.g. "101"), not an object
            string? legalForm = null;
            if (root.TryGetProperty("pravniForma", out var legalFormElement))
            {
                legalForm = legalFormElement.ValueKind == JsonValueKind.String
                    ? legalFormElement.GetString()
                    : legalFormElement.ToString();
            }

            return new AresCompanyInfo
            {
                RegistrationNumber = registrationNumber,
                CompanyName = companyName,
                TaxNumber = taxNumber,
                IsVatPayer = isVatPayer,
                Address = address,
                LegalForm = legalForm,
                IsSuccessful = true,
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing ARES response for IČO {RegistrationNumber}", registrationNumber);
            return new AresCompanyInfo
            {
                RegistrationNumber = registrationNumber,
                IsSuccessful = false,
                ErrorMessage = $"Error parsing ARES response: {ex.Message}",
                FetchedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// Builds street address string from ARES address components
    /// </summary>
    private string GetAddressString(JsonElement addressElement)
    {
        var parts = new List<string>();

        // Street name
        if (addressElement.TryGetProperty("nazevUlice", out var street))
        {
            var streetName = street.GetString();
            if (!string.IsNullOrEmpty(streetName))
                parts.Add(streetName);
        }

        // Building number (číslo popisné)
        if (addressElement.TryGetProperty("cisloDomovni", out var buildingNumber))
        {
            var number = buildingNumber.GetInt32();
            if (number > 0)
                parts.Add(number.ToString());
        }

        // Orientation number (číslo orientační) - ARES returns it as int
        if (addressElement.TryGetProperty("cisloOrientacni", out var orientationNumber))
        {
            var number = orientationNumber.ValueKind == JsonValueKind.String
                ? orientationNumber.GetString()
                : orientationNumber.ToString();
            if (!string.IsNullOrEmpty(number))
                parts.Add($"/{number}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Formats postal code from number to string with space
    /// Example: 12000 -> "120 00"
    /// </summary>
    private string FormatPostalCode(int psc)
    {
        var pscString = psc.ToString("D5"); // Pad with zeros to 5 digits
        return $"{pscString.Substring(0, 3)} {pscString.Substring(3, 2)}";
    }

    /// <summary>
    /// Saves result to cache.
    /// Cache write failures are non-fatal — if the database is unavailable,
    /// we log a warning and continue. The ARES result is still returned to the caller.
    /// </summary>
    private async Task CacheResult(AresCompanyInfo companyInfo, CancellationToken cancellationToken)
    {
        try
        {
            var cacheEntry = new AresCacheEntry
            {
                RegistrationNumber = companyInfo.RegistrationNumber,
                JsonData = JsonSerializer.Serialize(companyInfo),
                FetchedAt = companyInfo.FetchedAt,
                // Successful lookups cached for 30 days; failures only 1 hour
                // so that transient errors or newly registered companies aren't blocked.
                ExpiresAt = companyInfo.FetchedAt.Add(
                    companyInfo.IsSuccessful ? SuccessCacheExpiration : FailureCacheExpiration),
                IsSuccessful = companyInfo.IsSuccessful,
                ErrorMessage = companyInfo.ErrorMessage,
                CompanyName = companyInfo.CompanyName,
                TaxNumber = companyInfo.TaxNumber,
                IsVatPayer = companyInfo.IsVatPayer
            };

            await _cacheRepository.SaveCacheAsync(cacheEntry, cancellationToken);
        }
        catch (Exception ex)
        {
            // Cache save is best-effort. If the DB is unavailable, we still have
            // the ARES result in memory — just can't cache it for next time.
            _logger.LogWarning(ex,
                "Failed to save ARES cache for IČO {RegistrationNumber} — result not cached",
                companyInfo.RegistrationNumber);
        }
    }

    /// <summary>
    /// Deserializes cached data back to model
    /// </summary>
    private AresCompanyInfo DeserializeCachedData(AresCacheEntry cacheEntry)
    {
        try
        {
            var companyInfo = JsonSerializer.Deserialize<AresCompanyInfo>(cacheEntry.JsonData);
            return companyInfo ?? new AresCompanyInfo
            {
                RegistrationNumber = cacheEntry.RegistrationNumber,
                IsSuccessful = false,
                ErrorMessage = "Failed to deserialize cached data",
                FetchedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deserializing cached data for IČO {RegistrationNumber}", cacheEntry.RegistrationNumber);
            return new AresCompanyInfo
            {
                RegistrationNumber = cacheEntry.RegistrationNumber,
                IsSuccessful = false,
                ErrorMessage = $"Error reading cached data: {ex.Message}",
                FetchedAt = DateTime.UtcNow
            };
        }
    }
}

/// <summary>
/// Represents cached entry in database
/// </summary>
public class AresCacheEntry
{
    public string RegistrationNumber { get; set; } = string.Empty;
    public string JsonData { get; set; } = string.Empty;
    public DateTime FetchedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsSuccessful { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CompanyName { get; set; }
    public string? TaxNumber { get; set; }
    public bool? IsVatPayer { get; set; }
}

/// <summary>
/// Repository interface for ARES cache storage
/// Implementation will be in Infrastructure layer using EF Core
/// </summary>
public interface IAresCacheRepository
{
    Task<AresCacheEntry?> GetCachedDataAsync(string registrationNumber, CancellationToken cancellationToken = default);
    Task SaveCacheAsync(AresCacheEntry cacheEntry, CancellationToken cancellationToken = default);
}
