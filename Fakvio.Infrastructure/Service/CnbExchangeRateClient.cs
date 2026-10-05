using System.Globalization;
using Fakvio.Application.Service;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Downloads the ČNB daily fixing text file ("Kurzy devizového trhu - denní kurz").
/// Typed HttpClient registered with AddHttpClient. Format (verified against the live file):
/// <code>
/// 01.10.2026 #189
/// země|měna|množství|kód|kurz
/// Maďarsko|forint|100|HUF|6,660
/// </code>
/// Line 1 = declaration date + serial number, line 2 = header, then one currency per line with a
/// DECIMAL COMMA. Asking for a weekend, holiday, or a day whose fixing is not out yet returns the
/// last published fixing — its date is in line 1, which is why <see cref="CnbDailyRates.ValidFor"/>
/// comes from the file and not from the requested date.
/// </summary>
public class CnbExchangeRateClient : ICnbExchangeRateClient
{
    private const string BaseUrl =
        "https://www.cnb.cz/cs/financni-trhy/devizovy-trh/kurzy-devizoveho-trhu/kurzy-devizoveho-trhu/denni_kurz.txt";

    private readonly HttpClient _http;

    public CnbExchangeRateClient(HttpClient http) => _http = http;

    public async Task<CnbDailyRates> GetDailyRatesAsync(DateOnly date, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}?date={date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";
        string body;
        try
        {
            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"ČNB returned HTTP {(int)response.StatusCode} for {date:yyyy-MM-dd}.");
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"ČNB exchange rate service is unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("ČNB exchange rate service timed out.", ex);
        }

        return Parse(body);
    }

    /// <summary>
    /// Parses the file body. Public for unit tests. Throws <see cref="InvalidOperationException"/> when the
    /// header line is not "dd.MM.yyyy #n" or no currency line could be read (a changed format must fail
    /// loudly, not silently store nothing). Individual malformed lines are skipped.
    /// </summary>
    public static CnbDailyRates Parse(string body)
    {
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
            throw new InvalidOperationException("ČNB file is empty or truncated.");

        // "01.10.2026 #189" — only the date part matters.
        var datePart = lines[0].Split(' ', 2)[0];
        if (!DateOnly.TryParseExact(datePart, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var validFor))
            throw new InvalidOperationException($"ČNB file has an unexpected header '{lines[0]}'.");

        var rows = new List<CnbRateRow>();
        foreach (var line in lines.Skip(2)) // line 2 is the column header
        {
            var cols = line.Split('|');
            if (cols.Length != 5) continue;
            var code = cols[3].Trim().ToUpperInvariant();
            if (code.Length != 3 || !code.All(c => c is >= 'A' and <= 'Z')) continue;
            if (!int.TryParse(cols[2], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) || amount <= 0) continue;
            // Decimal comma in the source; accept a dot too, never a thousands separator.
            if (!decimal.TryParse(cols[4].Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate) || rate <= 0) continue;
            rows.Add(new CnbRateRow(code, amount, rate));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("ČNB file contained no readable rates.");
        return new CnbDailyRates(validFor, rows);
    }
}
