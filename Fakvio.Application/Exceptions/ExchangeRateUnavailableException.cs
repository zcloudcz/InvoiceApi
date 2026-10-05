namespace Fakvio.Application.Exceptions;

/// <summary>
/// No ČNB exchange rate could be determined for a foreign-currency document (ČNB unreachable and nothing cached,
/// document has no stored rate). A CZK tax return must never be built from a foreign amount treated as CZK, so
/// callers fail loudly; the API maps it to 409 EXCHANGE_RATE_UNAVAILABLE.
/// </summary>
public class ExchangeRateUnavailableException : InvalidOperationException
{
    public ExchangeRateUnavailableException(string currencyCode, DateOnly date)
        : base($"No ČNB rate for {currencyCode} on {date:yyyy-MM-dd}. Try again later or enter the rate on the document.") { }
}
