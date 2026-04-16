using Fakvio.Application.Service;

namespace Fakvio.Infrastructure.Service;

/// <summary>
/// Calculates statutory late payment interest per Czech Civil Code § 1970
/// and Government Regulation 351/2013 Sb.
///
/// Formula: Interest = principal × (rate / 100) × daysOverdue / 365
/// Rate = CNB repo rate (1st day of the half-year when delay started) + 8 percentage points.
///
/// The repo rate lookup table is hardcoded and must be updated twice a year (Jan 1, Jul 1).
/// For unknown future periods, the last known rate is used as a fallback.
///
/// Registered as Singleton — stateless, thread-safe, no dependencies.
/// </summary>
public class InterestCalculator : IInterestCalculator
{
    /// <summary>
    /// CNB repo rates by half-year period.
    /// Key: (Year, HalfYear) where HalfYear 1 = Jan–Jun, HalfYear 2 = Jul–Dec.
    /// Value: The repo rate as of the 1st day of that half-year.
    ///
    /// MAINTENANCE: Update this table twice a year with the latest CNB decision.
    /// Source: https://www.cnb.cz/cs/casto-kladene-dotazy/Jak-se-pocitaji-uroky-z-prodleni/
    /// </summary>
    private static readonly Dictionary<(int Year, int HalfYear), decimal> RepoRates = new()
    {
        { (2023, 1), 7.00m },   // 1H 2023
        { (2023, 2), 7.00m },   // 2H 2023
        { (2024, 1), 6.25m },   // 1H 2024
        { (2024, 2), 4.75m },   // 2H 2024
        { (2025, 1), 4.00m },   // 1H 2025
        { (2025, 2), 3.75m },   // 2H 2025
        { (2026, 1), 3.50m },   // 1H 2026
    };

    /// <summary>
    /// The statutory surcharge added to the CNB repo rate.
    /// Per § 2 of Government Regulation 351/2013 Sb.
    /// </summary>
    private const decimal SurchargePercentagePoints = 8.0m;

    /// <inheritdoc />
    public decimal Calculate(decimal principal, DateTime dueDate, DateTime calculationDate)
    {
        // No interest if not yet overdue or zero principal.
        if (calculationDate <= dueDate || principal <= 0)
            return 0m;

        var daysOverdue = (calculationDate - dueDate).Days;
        var rate = GetInterestRate(dueDate.AddDays(1)); // Delay starts the day after due date.

        // Interest = principal × (rate / 100) × daysOverdue / 365
        var interest = principal * (rate / 100m) * daysOverdue / 365m;

        // Round to 2 decimal places (standard Czech accounting rounding).
        return Math.Round(interest, 2, MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    public decimal GetInterestRate(DateTime delayStartDate)
    {
        var halfYear = delayStartDate.Month <= 6 ? 1 : 2;
        var repoRate = GetRepoRate(delayStartDate.Year, halfYear);
        return repoRate + SurchargePercentagePoints;
    }

    /// <summary>
    /// Looks up the CNB repo rate for the given half-year.
    /// If the exact period is not in the table (future dates), falls back to the last known rate.
    /// </summary>
    private static decimal GetRepoRate(int year, int halfYear)
    {
        if (RepoRates.TryGetValue((year, halfYear), out var rate))
            return rate;

        // Fallback: find the most recent known rate.
        // Sort by (Year, HalfYear) descending and take the first.
        var lastKnown = RepoRates
            .OrderByDescending(kv => kv.Key.Year)
            .ThenByDescending(kv => kv.Key.HalfYear)
            .FirstOrDefault();

        return lastKnown.Value; // Returns 0 if table is completely empty (should never happen).
    }
}
