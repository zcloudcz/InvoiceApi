using Fakvio.Contracts.Dto.Vies;

namespace Fakvio.Application.Service;

/// <summary>
/// Verifies EU VAT identification numbers (DIČ) against VIES (VAT Information Exchange System),
/// the European Commission's registry — <see href="https://ec.europa.eu/taxation_customs/vies/"/>.
///
/// Unlike ARES (<see cref="IAresService"/> equivalent, "AresService.IAresService"), which only
/// covers Czech IČO, VIES validates any EU member state's VAT number and tells us whether it is
/// currently registered — useful before invoicing an EU client under the reverse-charge regime.
/// </summary>
public interface IViesService
{
    /// <summary>
    /// Verifies a VAT ID (e.g. "CZ12345678", "DE 123 456 789"). Accepts common formatting —
    /// spaces/dashes and mixed case are normalized before the country code and number are split.
    /// Successful ("Valid") results are cached for 24h; failures are never cached, so a transient
    /// VIES outage does not block a retry.
    /// </summary>
    /// <param name="vatId">VAT ID including its 2-letter country prefix.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ViesVerificationResult> VerifyAsync(string vatId, CancellationToken cancellationToken = default);
}
