namespace Fakvio.Domain.Enums;

/// <summary>
/// VAT rate category used by the EU OSS (One-Stop-Shop) code table.
/// Each EU member state defines its own standard/reduced/super-reduced/parking rates —
/// this enum just labels which bucket a given <see cref="Fakvio.Domain.Entities.OssVatRate"/>
/// row belongs to, for display purposes. The actual percentage is the Rate column.
/// </summary>
public enum EOssVatRateCategory
{
    /// <summary>Standard rate — applies to most goods/services (e.g. DE 19%, FR 20%).</summary>
    Standard = 0,

    /// <summary>Reduced rate — applies to specific categories (e.g. food, books).</summary>
    Reduced = 1,

    /// <summary>Super-reduced rate — a handful of states have a second, lower reduced rate.</summary>
    SuperReduced = 2,

    /// <summary>"Parking" rate — transitional rate some states kept for specific goods (e.g. Belgium, Ireland).</summary>
    Parking = 3
}
