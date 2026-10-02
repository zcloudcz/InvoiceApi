namespace Fakvio.Contracts.Common;

/// <summary>
/// Single place deciding whether an invoice's payment QR is the SEPA EPC format (otherwise the Czech
/// SPD / Paylibo / SIND path). Lives in Contracts so the server (QR generation, PDF label) and the
/// UI (card title) all consume the same decision instead of re-deriving it.
/// </summary>
public static class EpcQrPolicy
{
    public const decimal MinAmount = 0.01m;
    public const decimal MaxAmount = 999999999.99m;

    /// <summary>EPC069-12 only allows amounts 0.01 - 999,999,999.99 (so no credit notes / zero totals).</summary>
    public static bool IsAmountSupported(decimal total) => total >= MinAmount && total <= MaxAmount;

    /// <summary>EPC is used only for EUR (case-insensitive) invoices with an IBAN and a supported amount.</summary>
    public static bool UsesEpc(string? currencyCode, string? iban, decimal total) =>
        !string.IsNullOrWhiteSpace(iban)
        && string.Equals(currencyCode, "EUR", StringComparison.OrdinalIgnoreCase)
        && IsAmountSupported(total);
}
