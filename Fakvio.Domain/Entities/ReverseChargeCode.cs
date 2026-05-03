using Fakvio.Domain.Common;

namespace Fakvio.Domain.Entities;

/// <summary>
/// Lookup table for reverse charge codes (kódy předmětu plnění v režimu přenesení daňové povinnosti).
///
/// In Czech VAT law (§92a–§92e ZDPH), certain goods and services must be reported
/// under the reverse charge mechanism (přenesení daňové povinnosti, PDP/PDV).
/// Each type of supply has a numeric code assigned by MFČR (Ministry of Finance).
///
/// These codes appear in the VAT control statement (kontrolní hlášení / EPO XML):
///   - Supplier reports in section A.1
///   - Buyer reports in section B.1
///
/// Source: Příloha č. 6 ZDPH (zákon č. 235/2004 Sb.) a pokyn GFŘ D-59
/// https://www.financnisprava.cz/cs/dane/dane/dan-z-pridane-hodnoty/kontrolni-hlaseni
/// </summary>
public class ReverseChargeCode : BaseEntity
{
    /// <summary>
    /// Official MFČR code for this type of supply.
    /// Examples: "1", "1a", "3", "3a", "4", "5", "6", "7", "11", "12", "13", "14", "21", "25"
    /// Max 8 characters to accommodate alphanumeric codes like "1a" and "3a".
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Czech name of the supply type.
    /// Required — always present in the MFČR numbering (číselník).
    /// Example: "Zlato" (gold), "Stavební nebo montážní práce" (construction/assembly work)
    /// </summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>
    /// English name of the supply type.
    /// Optional — useful for multi-language UI and international auditors.
    /// Example: "Gold", "Construction or assembly work"
    /// </summary>
    public string? NameEn { get; set; }

    /// <summary>
    /// Reference to the relevant paragraph in ZDPH (Czech VAT Act) that mandates this code.
    /// Values: "§92a", "§92b", "§92c", "§92d", "§92e"
    ///
    /// §92a — general reverse charge provision (enabling clause)
    /// §92b — gold (zlato)
    /// §92c — waste and scrap (odpady a šrot), emission allowances (emisní povolenky), mobile devices, etc.
    /// §92d — construction and assembly work (stavební a montážní práce)
    /// §92e — transfer of greenhouse gas emission allowances
    /// </summary>
    public string ParagraphRef { get; set; } = string.Empty;

    /// <summary>
    /// Date from which this code is valid.
    /// Stored as DateOnly — validity is defined by legislative date, not time-of-day.
    /// </summary>
    public DateOnly ValidFrom { get; set; }

    /// <summary>
    /// Date until which this code is valid (inclusive).
    /// Null = valid indefinitely (until MFČR publishes a new revision).
    /// When MFČR removes or replaces a code, set ValidTo to the last day of validity.
    /// </summary>
    public DateOnly? ValidTo { get; set; }

    /// <summary>
    /// Whether this code is currently active and should be offered in the UI.
    /// Soft-disable a code instead of deleting it to preserve historical references on invoices.
    /// A record may be active but outside its ValidFrom/ValidTo window if the window reflects
    /// a future or historical legislative period.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
