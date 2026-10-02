namespace Fakvio.Domain.Enums;

/// <summary>
/// Target accounting system for the "Export do účetnictví" feature.
/// Each value maps to one <c>IAccountingExporter</c> implementation
/// (Fakvio.Infrastructure/Service/AccountingExport/).
/// Existing values must NEVER be renumbered — the integer can be persisted/logged.
/// Localization key pattern: EAccountingSystem_{Value} (e.g. EAccountingSystem_Pohoda).
/// </summary>
public enum EAccountingSystem
{
    /// <summary>Stormware POHODA — dataPack / inv:invoice XML (version 2.0 schema).</summary>
    Pohoda = 1,

    /// <summary>Money S3 (Seyfor/Money.cz) — MoneyData XML import format.</summary>
    MoneyS3 = 2,

    /// <summary>ABRA Flexi — winstrom XML (faktura-vydana / faktura-prijata).</summary>
    AbraFlexi = 3
}
