using System.Globalization;
using Fakvio.Domain.Entities;
using Fakvio.Infrastructure.Service.Ubl;

namespace Fakvio.Infrastructure.Service.AccountingExport;

/// <summary>
/// Shared helpers used by all three <see cref="Fakvio.Application.Service.IAccountingExporter"/>
/// implementations (Pohoda, Money S3, ABRA Flexi) — VAT rate bucketing, bank account parsing,
/// culture-invariant decimal formatting. Kept here instead of duplicated per-mapper, same spirit
/// as IsdocMapper's private helpers but shared because all three exports need the same facts.
/// </summary>
internal static class AccountingExportCommon
{
    /// <summary>
    /// Czech VAT-rate bucket used by Pohoda (rateVAT) and, loosely, by Money S3/Flexi too.
    /// Fakvio stores the exact percentage per line (VatRatePercentage), not a rate "slot" —
    /// so this buckets by magnitude relative to the standard CZ rate (21 %):
    /// &gt;= 21 % → High (základní), 0 % &lt; rate &lt; 21 % → Low (snížená), 0 % → None.
    /// Known gap: there is no distinct "third rate" (třetí sazba) bucket because Fakvio's
    /// VatRate table does not model one — see DEVGUIDE §4.15.
    /// </summary>
    internal enum VatBucket { None, Low, High }

    internal static VatBucket ClassifyVatRate(decimal ratePercentage) =>
        ratePercentage <= 0 ? VatBucket.None
        : ratePercentage >= 21 ? VatBucket.High
        : VatBucket.Low;

    /// <summary>Parses the Czech "number/bankCode" bank account format into its two parts.</summary>
    internal static (string AccountNumber, string BankCode) ParseBankAccount(string? bankAccount)
    {
        if (string.IsNullOrWhiteSpace(bankAccount)) return (string.Empty, string.Empty);
        var idx = bankAccount.IndexOf('/');
        return idx < 0 ? (bankAccount, string.Empty) : (bankAccount[..idx], bankAccount[(idx + 1)..]);
    }

    internal static string FormatDecimal(decimal value) => value.ToString("F2", CultureInfo.InvariantCulture);

    internal static string FormatDate(DateTime? date) =>
        date.HasValue ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Primary (or first) address of a client, same fallback rule used by IsdocMapper/UblMapper.</summary>
    internal static Address? PrimaryAddress(Client? client) =>
        client?.Address?.FirstOrDefault(a => a.IsPrimary) ?? client?.Address?.FirstOrDefault();

    /// <summary>ISO 3166-1 alpha-2 country code, reusing the same table as ISDOC/UBL exports.</summary>
    internal static string CountryIso2(Address? address) => UblCodes.CountryToIso2(address?.Country) ?? "CZ";
}
