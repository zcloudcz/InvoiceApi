namespace Fakvio.Domain.Enums;

/// <summary>
/// Type of self-employed activity — determines the applicable lump-sum expense percentage (CZ).
/// Also used to determine flat-rate tax band eligibility.
///
/// Localization key pattern: EActivityType_{Value} (e.g., EActivityType_CraftTrade).
/// </summary>
public enum EActivityType
{
    /// <summary>
    /// Řemeslná živnost — craft trade (e.g., plumber, electrician, baker, hairdresser).
    /// Qualifies for 80% lump-sum expenses in CZ.
    /// </summary>
    CraftTrade = 1,

    /// <summary>
    /// Volná živnost — non-craft trade (e.g., IT consultant, e-commerce, financial advisor).
    /// Qualifies for 60% lump-sum expenses in CZ and SK.
    /// </summary>
    NonCraftTrade = 2,

    /// <summary>
    /// Svobodné povolání — regulated/liberal profession (e.g., lawyer, architect, doctor, auditor).
    /// Qualifies for 40% lump-sum expenses in CZ.
    /// </summary>
    RegulatedProfession = 3,

    /// <summary>
    /// Pronájem — rental income from property.
    /// Qualifies for 30% lump-sum expenses in CZ.
    /// </summary>
    Rental = 4
}
