using Fakvio.Domain.Entities;

namespace Fakvio.Application.Service;

/// <summary>
/// Abstraction for fetching CNB (Czech National Bank) exchange rates from an external source.
/// The default implementation (<c>CnbExchangeRateProvider</c>) downloads and parses
/// the CNB daily FX list (<c>denni_kurz.txt</c>).
///
/// Keeping the interface in Application layer lets the domain/application logic depend
/// on the contract without knowing about HTTP or CNB CSV format.
/// </summary>
public interface IExchangeRateProvider
{
    /// <summary>
    /// Downloads and parses today's CNB exchange rates.
    /// </summary>
    /// <param name="date">
    /// The date to fetch. CNB publishes rates for working days only.
    /// If <paramref name="date"/> falls on a weekend or public holiday,
    /// CNB returns the most recent available rates — the provider reflects that.
    /// Null means today's date.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// List of <see cref="ExchangeRate"/> records (one per currency in the CNB list).
    /// The list does NOT include CZK (always 1:1 by definition).
    /// </returns>
    /// <exception cref="HttpRequestException">When the CNB endpoint is unreachable.</exception>
    /// <exception cref="FormatException">When the response does not match the expected format.</exception>
    Task<List<ExchangeRate>> FetchRatesAsync(DateOnly? date = null, CancellationToken cancellationToken = default);
}
