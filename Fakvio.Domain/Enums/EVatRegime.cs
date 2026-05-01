namespace Fakvio.Domain.Enums;

/// <summary>
/// Defines the VAT accounting regime for a single invoice line item.
/// Used to support Reverse Charge (přenesená daňová povinnost / PDP, §92a–92e ZDPH)
/// and other special VAT treatments at the line-item level.
///
/// Default is <see cref="Standard"/> (backward-compatible — existing items are unaffected).
/// Stored as an integer column in the database (NOT NULL, default 0 = Standard).
///
/// Localization key pattern: EVatRegime_{Value} (e.g., EVatRegime_ReverseCharge)
/// </summary>
public enum EVatRegime
{
    /// <summary>
    /// Normal VAT regime — supplier charges and remits VAT.
    /// This is the default for all existing and new items unless explicitly set.
    /// </summary>
    Standard = 0,

    /// <summary>
    /// Přenesená daňová povinnost (PDP / Reverse Charge §92a–92e ZDPH).
    /// VAT is not charged by the supplier; the recipient (buyer) self-accounts
    /// for VAT. Required for specific services (construction, IT commodity codes, etc.).
    /// </summary>
    ReverseCharge = 1,

    /// <summary>
    /// VAT-exempt supply — no VAT charged and not recoverable by supplier.
    /// Example: financial services, certain medical services, exports.
    /// </summary>
    Exempt = 2,

    /// <summary>
    /// Supply outside the scope of VAT entirely.
    /// Example: non-business activities, inter-company recharges outside CZ/EU.
    /// </summary>
    OutOfScope = 3
}
