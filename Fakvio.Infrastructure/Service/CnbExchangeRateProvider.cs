using System.Globalization;
using System.Net.Http;
using Fakvio.Application.Service;
using Fakvio.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Downloads and parses CNB (Czech National Bank) daily exchange rates.
///
/// CNB publishes rates at https://www.cnb.cz/cs/financni-trhy/devizovy-trh/kurzy-devizoveho-trhu/denni-kurz.txt
/// The file format (as of 2025) looks like:
/// <code>
/// 02.05.2025 #85
/// země|měna|množství|kód|kurz
/// Austrálie|dolar|1|AUD|14,617
/// EMU|euro|1|EUR|25,255
/// ...
/// </code>
///
/// Column mapping (semicolon-separated in the data rows):
/// index 0 = country name (ignored)
/// index 1 = currency name (ignored, we use the code)
/// index 2 = amount (CNB unit count, integer)
/// index 3 = ISO 4217 code
/// index 4 = rate (CZK per Amount units, uses comma as decimal separator)
///
/// CNB only publishes on working days. For weekends / public holidays the
/// response still contains the most recent business-day rates.
/// </summary>
public class CnbExchangeRateProvider : IExchangeRateProvider
{
    // Named HttpClient registered in DI as "CnbHttpClient" with base address.
    private readonly HttpClient _httpClient;
    private readonly ILogger<CnbExchangeRateProvider> _logger;

    /// <summary>
    /// CNB daily rate URL template. Date param is optional — omitting it gives today's rates.
    /// When a specific date is requested, CNB returns the rate list valid on that date
    /// (falls back to the previous business day automatically).
    /// </summary>
    private const string RateUrlTemplate = "https://www.cnb.cz/cs/financni-trhy/devizovy-trh/kurzy-devizoveho-trhu/denni-kurz.txt?date={0:dd.MM.yyyy}";

    /// <summary>
    /// Minimum number of data rows expected in a valid CNB response.
    /// The CNB list normally contains 30+ currencies; fewer suggests a malformed response.
    /// </summary>
    private const int MinExpectedRateCount = 5;

    public CnbExchangeRateProvider(HttpClient httpClient, ILogger<CnbExchangeRateProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<List<ExchangeRate>> FetchRatesAsync(
        DateOnly? date = null,
        CancellationToken cancellationToken = default)
    {
        // Build the URL — if no date requested, CNB returns today's rates.
        var url = date.HasValue
            ? string.Format(RateUrlTemplate, date.Value.ToDateTime(TimeOnly.MinValue))
            : "https://www.cnb.cz/cs/financni-trhy/devizovy-trh/kurzy-devizoveho-trhu/denni-kurz.txt";

        _logger.LogInformation("CnbExchangeRateProvider: fetching rates from {Url}", url);

        var content = await _httpClient.GetStringAsync(url, cancellationToken);
        return ParseCnbText(content, date);
    }

    /// <summary>
    /// Parses the CNB plain-text rate list into <see cref="ExchangeRate"/> objects.
    ///
    /// Expected format (lines separated by \n, columns separated by |):
    /// Line 0: "DD.MM.YYYY #N"  (header with date and sequence number)
    /// Line 1: "country|currency_name|amount|code|rate"  (column header — skip)
    /// Lines 2+: one rate per line
    /// </summary>
    internal static List<ExchangeRate> ParseCnbText(string cnbText, DateOnly? requestedDate)
    {
        if (string.IsNullOrWhiteSpace(cnbText))
            throw new FormatException("CNB response is empty.");

        // Normalize line endings — CNB may use \r\n or \n
        var lines = cnbText
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length < 3)
            throw new FormatException($"CNB response has too few lines ({lines.Length}); expected header + column row + data rows.");

        // Parse the publication date from the first line ("02.05.2025 #85")
        var headerParts = lines[0].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!DateTime.TryParseExact(
                headerParts[0], "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None,
                out var publicationDate))
        {
            throw new FormatException($"CNB header date '{headerParts[0]}' could not be parsed as dd.MM.yyyy.");
        }

        // Use the actual publication date from the response (CNB auto-falls-back on weekends).
        var validFrom = DateOnly.FromDateTime(publicationDate);

        // Line 1 is the column header — skip it.
        var rates = new List<ExchangeRate>();

        // Czech decimal separator is a comma (','); parse with CzechCulture or manually replace.
        var czechCulture = new CultureInfo("cs-CZ");

        for (int i = 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
                continue;

            var parts = line.Split('|');
            if (parts.Length != 5)
            {
                // Malformed line — skip and continue; don't abort the entire import.
                continue;
            }

            // parts: [country, currency_name, amount, code, rate]
            var amountStr = parts[2].Trim();
            var code = parts[3].Trim().ToUpperInvariant();
            var rateStr = parts[4].Trim();

            if (!int.TryParse(amountStr, out var amount) || amount <= 0)
                continue;

            if (!decimal.TryParse(rateStr, NumberStyles.Number, czechCulture, out var rate) || rate <= 0)
                continue;

            if (string.IsNullOrEmpty(code) || code.Length != 3)
                continue;

            rates.Add(new ExchangeRate
            {
                CurrencyCode = code,
                ValidFrom = validFrom,
                Rate = rate,
                Amount = amount,
                Source = "CNB"
            });
        }

        if (rates.Count < MinExpectedRateCount)
        {
            throw new FormatException(
                $"CNB response contained only {rates.Count} valid rate rows (minimum expected: {MinExpectedRateCount}). " +
                $"The format may have changed.");
        }

        return rates;
    }
}
